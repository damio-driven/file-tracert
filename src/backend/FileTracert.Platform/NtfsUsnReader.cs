using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FileTracert.Contracts.Platform;
using FileTracert.Platform.Internal;
using FileTracert.Platform.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace FileTracert.Platform;

/// <summary>
/// NTFS USN change-journal reader: full MFT snapshot, incremental delta read,
/// and wrap detection. All native calls stay inside this assembly.
/// </summary>
/// <remarks>
/// Path reconstruction for the full snapshot is exact (the whole MFT is in the
/// map). For the incremental read, parents are usually outside the delta, so
/// <see cref="UsnEntry.RelativePath"/> is best-effort (leaf name when unresolved);
/// Business re-resolves it with a DB-backed fallback (see <see cref="UsnPathResolver"/>).
/// </remarks>
internal sealed class NtfsUsnReader : IUsnReader
{
    /// <summary>The NTFS root directory always lives at MFT record index 5.</summary>
    private const ulong RootMftIndex = 5;

    /// <summary>
    /// A USN file reference number packs a sequence number in the high 16 bits
    /// and the MFT record index in the low 48 bits. The root's full FRN therefore
    /// is <em>not</em> 5 — it carries a sequence number — so we identify it by its
    /// masked index.
    /// </summary>
    private const ulong MftRecordIndexMask = 0x0000_FFFF_FFFF_FFFF;

    private const int BufferSize = 1 << 20; // 1 MiB
    private const int CancellationCheckInterval = 4096;

    private readonly ILogger<NtfsUsnReader> _logger;

    public NtfsUsnReader(ILogger<NtfsUsnReader> logger)
    {
        _logger = logger;
    }

    public bool SupportsUsn(string volumeGuid)
    {
        using var handle = OpenVolume(volumeGuid);
        if (handle.IsInvalid)
        {
            return false;
        }

        if (TryQueryJournal(handle, out _, out var error))
        {
            return true;
        }

        // NTFS without an active journal still "supports" USN — it is creatable.
        return error is NativeMethods.ErrorJournalNotActive
            or NativeMethods.ErrorJournalDeleteInProgress
            or NativeMethods.ErrorJournalEntryDeleted;
    }

    public UsnJournalState GetJournalState(string volumeGuid)
    {
        using var handle = OpenVolumeOrThrow(volumeGuid);
        if (!TryQueryJournal(handle, out var data, out var error))
        {
            throw new Win32Exception(error, $"FSCTL_QUERY_USN_JOURNAL failed for {volumeGuid}.");
        }

        return new UsnJournalState(data.UsnJournalId, data.FirstUsn, data.NextUsn, data.LowestValidUsn);
    }

    public unsafe void EnsureJournal(string volumeGuid)
    {
        using var handle = OpenVolumeOrThrow(volumeGuid);
        if (TryQueryJournal(handle, out _, out _))
        {
            return; // already active
        }

        // MaximumSize/AllocationDelta = 0 → let NTFS pick sensible defaults.
        var create = new CreateUsnJournalData { MaximumSize = 0, AllocationDelta = 0 };
        if (!NativeMethods.DeviceIoControl(
                handle,
                NativeMethods.FsctlCreateUsnJournal,
                &create,
                (uint)sizeof(CreateUsnJournalData),
                null,
                0,
                out _,
                IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"FSCTL_CREATE_USN_JOURNAL failed for {volumeGuid} (requires elevation).");
        }
    }

    public IEnumerable<UsnEntry> ReadFullSnapshot(string volumeGuid, CancellationToken ct) =>
        ReadFullSnapshotCore(volumeGuid, ct);

    public UsnChangeResult ReadChanges(string volumeGuid, long sinceUsn, ulong journalId, CancellationToken ct)
    {
        using var handle = OpenVolumeOrThrow(volumeGuid);
        if (!TryQueryJournal(handle, out var state, out var error))
        {
            throw new Win32Exception(error, $"FSCTL_QUERY_USN_JOURNAL failed for {volumeGuid}.");
        }

        // The journal was deleted/recreated, or our cursor predates the oldest
        // surviving record → we cannot trust a delta, force a full rescan.
        if (state.UsnJournalId != journalId || sinceUsn < state.LowestValidUsn)
        {
            return new UsnChangeResult(Array.Empty<UsnChangeRecord>(), state.NextUsn, RequiresFullRescan: true);
        }

        var changes = ReadChangesCore(handle, sinceUsn, journalId, ct, out var nextUsn);
        return new UsnChangeResult(changes, nextUsn, RequiresFullRescan: false);
    }

    // ---- core readers (non-iterator so they may contain unsafe code) ----

    private unsafe List<UsnEntry> ReadFullSnapshotCore(string volumeGuid, CancellationToken ct)
    {
        using var handle = OpenVolumeOrThrow(volumeGuid);

        // Pass 1: read every MFT record into FRN-keyed maps.
        var nodes = new Dictionary<ulong, FrnNode>();
        var extras = new Dictionary<ulong, (FileAttributes Attributes, long Usn)>();
        EnumerateMft(handle, nodes, extras, ct);

        // Pass 2: reconstruct each path and project to UsnEntry.
        var resolver = new UsnPathResolver(nodes, DetermineRootFrn(nodes));
        var result = new List<UsnEntry>(nodes.Count);
        var counter = 0;

        foreach (var (frn, node) in nodes)
        {
            if (++counter % CancellationCheckInterval == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (!resolver.TryResolve(frn, out var relativePath))
            {
                _logger.LogDebug("Skipping orphan FRN {Frn} on {Volume}.", frn, volumeGuid);
                continue;
            }

            var extra = extras[frn];
            result.Add(new UsnEntry(
                frn,
                node.ParentFrn,
                node.Name,
                relativePath,
                node.IsDirectory,
                SizeBytes: null, // USN carries no size; Business fills it later
                extra.Attributes,
                extra.Usn));
        }

        return result;
    }

    /// <summary>
    /// Finds the volume root's full FRN. The MFT enumeration may not return the
    /// root's own record, so we fall back to any entry whose parent resolves to
    /// MFT index 5.
    /// </summary>
    private static ulong DetermineRootFrn(Dictionary<ulong, FrnNode> nodes)
    {
        foreach (var (frn, node) in nodes)
        {
            if ((frn & MftRecordIndexMask) == RootMftIndex)
            {
                return frn;
            }

            if ((node.ParentFrn & MftRecordIndexMask) == RootMftIndex)
            {
                return node.ParentFrn;
            }
        }

        return RootMftIndex;
    }

    private unsafe void EnumerateMft(
        SafeFileHandle handle,
        Dictionary<ulong, FrnNode> nodes,
        Dictionary<ulong, (FileAttributes, long)> extras,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var input = new MftEnumDataV0
            {
                StartFileReferenceNumber = 0,
                LowUsn = 0,
                HighUsn = long.MaxValue
            };

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                uint bytesReturned;
                bool ok;
                fixed (byte* outPtr = buffer)
                {
                    ok = NativeMethods.DeviceIoControl(
                        handle,
                        NativeMethods.FsctlEnumUsnData,
                        &input,
                        (uint)sizeof(MftEnumDataV0),
                        outPtr,
                        (uint)buffer.Length,
                        out bytesReturned,
                        IntPtr.Zero);
                }

                if (!ok)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == NativeMethods.ErrorHandleEof)
                    {
                        break; // walked the whole MFT
                    }

                    throw new Win32Exception(error, "FSCTL_ENUM_USN_DATA failed.");
                }

                if (bytesReturned <= sizeof(ulong))
                {
                    break; // only the next-FRN cursor, no records
                }

                // First 8 bytes are the next StartFileReferenceNumber.
                input.StartFileReferenceNumber =
                    BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, sizeof(ulong)));

                ParseRecords(buffer.AsSpan(sizeof(ulong), (int)bytesReturned - sizeof(ulong)), nodes, extras);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ParseRecords(
        ReadOnlySpan<byte> records,
        Dictionary<ulong, FrnNode> nodes,
        Dictionary<ulong, (FileAttributes, long)> extras)
    {
        var offset = 0;
        while (offset + UsnRecordParser.HeaderLength <= records.Length)
        {
            var span = records[offset..];
            var recordLength = (int)UsnRecordParser.ReadRecordLength(span);
            if (recordLength <= 0 || offset + recordLength > records.Length)
            {
                break;
            }

            if (UsnRecordParser.ReadMajorVersion(span) == UsnRecordParser.SupportedMajorVersion)
            {
                var record = UsnRecordParser.Parse(span[..recordLength]);
                var isDirectory = record.Attributes.HasFlag(FileAttributes.Directory);
                nodes[record.FileReferenceNumber] =
                    new FrnNode(record.Name, record.ParentFileReferenceNumber, isDirectory);
                extras[record.FileReferenceNumber] = (record.Attributes, record.Usn);
            }

            offset += recordLength;
        }
    }

    private unsafe List<UsnChangeRecord> ReadChangesCore(
        SafeFileHandle handle,
        long sinceUsn,
        ulong journalId,
        CancellationToken ct,
        out long nextUsn)
    {
        var changes = new List<UsnChangeRecord>();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        nextUsn = sinceUsn;

        try
        {
            var input = new ReadUsnJournalDataV0
            {
                StartUsn = sinceUsn,
                ReasonMask = 0xFFFFFFFF,
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalId = journalId
            };

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                uint bytesReturned;
                bool ok;
                fixed (byte* outPtr = buffer)
                {
                    ok = NativeMethods.DeviceIoControl(
                        handle,
                        NativeMethods.FsctlReadUsnJournal,
                        &input,
                        (uint)sizeof(ReadUsnJournalDataV0),
                        outPtr,
                        (uint)buffer.Length,
                        out bytesReturned,
                        IntPtr.Zero);
                }

                if (!ok)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_READ_USN_JOURNAL failed.");
                }

                nextUsn = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0, sizeof(long)));
                if (bytesReturned <= sizeof(long))
                {
                    break; // only the next-USN cursor, no more records
                }

                ParseChangeRecords(buffer.AsSpan(sizeof(long), (int)bytesReturned - sizeof(long)), changes);

                // Continue from the new cursor.
                input.StartUsn = nextUsn;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return changes;
    }

    private static void ParseChangeRecords(ReadOnlySpan<byte> records, List<UsnChangeRecord> changes)
    {
        var offset = 0;
        while (offset + UsnRecordParser.HeaderLength <= records.Length)
        {
            var span = records[offset..];
            var recordLength = (int)UsnRecordParser.ReadRecordLength(span);
            if (recordLength <= 0 || offset + recordLength > records.Length)
            {
                break;
            }

            if (UsnRecordParser.ReadMajorVersion(span) == UsnRecordParser.SupportedMajorVersion)
            {
                var record = UsnRecordParser.Parse(span[..recordLength]);
                var reason = (UsnReason)record.Reason;
                var isDirectory = record.Attributes.HasFlag(FileAttributes.Directory);
                var isRename = (reason & (UsnReason.RenameOldName | UsnReason.RenameNewName)) != 0;
                var oldName = (reason & UsnReason.RenameOldName) != 0 ? record.Name : null;

                var entry = new UsnEntry(
                    record.FileReferenceNumber,
                    record.ParentFileReferenceNumber,
                    record.Name,
                    record.Name, // best-effort; Business re-resolves the full path
                    isDirectory,
                    SizeBytes: null,
                    record.Attributes,
                    record.Usn);

                changes.Add(new UsnChangeRecord(entry, reason, isRename, oldName));
            }

            offset += recordLength;
        }
    }

    // ---- handle helpers ----

    private static SafeFileHandle OpenVolume(string volumeGuid)
    {
        // CreateFile wants the GUID path WITHOUT the trailing backslash.
        var devicePath = volumeGuid.TrimEnd('\\');
        return NativeMethods.CreateFile(
            devicePath,
            NativeMethods.GenericRead,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics,
            IntPtr.Zero);
    }

    private static SafeFileHandle OpenVolumeOrThrow(string volumeGuid)
    {
        var handle = OpenVolume(volumeGuid);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Could not open volume {volumeGuid}.");
        }

        return handle;
    }

    private static unsafe bool TryQueryJournal(SafeFileHandle handle, out UsnJournalDataV0 data, out int error)
    {
        UsnJournalDataV0 local = default;
        var ok = NativeMethods.DeviceIoControl(
            handle,
            NativeMethods.FsctlQueryUsnJournal,
            null,
            0,
            &local,
            (uint)sizeof(UsnJournalDataV0),
            out _,
            IntPtr.Zero);

        error = ok ? 0 : Marshal.GetLastWin32Error();
        data = local;
        return ok;
    }
}
