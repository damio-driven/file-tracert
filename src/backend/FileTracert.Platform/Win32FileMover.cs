using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FileTracert.Contracts.Platform;
using FileTracert.Platform.Internal;
using FileTracert.Platform.Interop;
using Microsoft.Extensions.Logging;

namespace FileTracert.Platform;

/// <summary>
/// Win32 implementation of <see cref="IFileMover"/>. Resolves volume GUIDs to
/// mount points via <c>GetVolumePathNamesForVolumeName</c> and delegates all
/// mutations to standard .NET IO + Shell32 (recycle bin).
/// No Business code ever calls System.IO directly — this class is the single
/// door into file-system mutations.
/// </summary>
internal sealed class Win32FileMover : IFileMover
{
    private const int CopyBufferSize = 81_920; // 80 KB — good balance for both HDD and SSD

    private readonly ILogger<Win32FileMover> _logger;

    public Win32FileMover(ILogger<Win32FileMover> logger) => _logger = logger;

    public void CreateFolder(string volumeGuid, string relativePath)
    {
        var full = Resolve(volumeGuid, relativePath);
        Directory.CreateDirectory(full);
    }

    public void RenameIntraVolume(string volumeGuid, string relativePath, string newName)
    {
        var full = Resolve(volumeGuid, relativePath);
        var dest = Path.Combine(Path.GetDirectoryName(full)!, newName);
        if (!IsCaseOnlyDifference(full, dest))
            ThrowIfDestinationOccupied(dest);

        if (Directory.Exists(full))
            Directory.Move(full, dest);
        else
            File.Move(full, dest);
    }

    public void MoveIntraVolume(string volumeGuid, string srcRel, string destRel)
    {
        var src = Resolve(volumeGuid, srcRel);
        var dest = Resolve(volumeGuid, destRel);
        if (!IsCaseOnlyDifference(src, dest))
            ThrowIfDestinationOccupied(dest);
        EnsureParent(dest);

        if (Directory.Exists(src))
            Directory.Move(src, dest);
        else
            File.Move(src, dest);
    }

    public async Task CopyFileAsync(string srcVolGuid, string srcRel,
                                     string destVolGuid, string destPartialRel,
                                     Func<long, CancellationToken, Task>? onProgress, CancellationToken ct)
    {
        var src = Resolve(srcVolGuid, srcRel);
        var dest = Resolve(destVolGuid, destPartialRel);
        EnsureParent(dest);

        await using var srcStream = new FileStream(
            src, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var dstStream = new FileStream(
            dest, FileMode.Create, FileAccess.Write, FileShare.None,
            CopyBufferSize, FileOptions.Asynchronous);

        var buf = new byte[CopyBufferSize];
        long totalCopied = 0;
        int read;
        while ((read = await srcStream.ReadAsync(buf, ct)) > 0)
        {
            await dstStream.WriteAsync(buf.AsMemory(0, read), ct);
            totalCopied += read;
            if (onProgress is not null)
                await onProgress(totalCopied, ct);
        }
    }

    public bool Verify(string aVolGuid, string aRel, string bVolGuid, string bRel, bool withHash)
    {
        var aPath = Resolve(aVolGuid, aRel);
        var bPath = Resolve(bVolGuid, bRel);

        var aInfo = new FileInfo(aPath);
        var bInfo = new FileInfo(bPath);
        if (!aInfo.Exists || !bInfo.Exists) return false;
        if (aInfo.Length != bInfo.Length) return false;
        if (!withHash) return true;

        return ComputeSha256(aPath) == ComputeSha256(bPath);
    }

    public void FinalizePartial(string volGuid, string partialRel, string finalRel)
    {
        var partial = Resolve(volGuid, partialRel);
        var final = Resolve(volGuid, finalRel);
        ThrowIfDestinationOccupied(final);

        EnsureParent(final);
        File.Move(partial, final);
    }

    /// <summary>
    /// C20: an occupied destination is a typed <see cref="NameCollisionException"/> — the
    /// engine maps it to Blocked(NameCollision), reactivatable — never a raw IOException
    /// that would end the job in terminal Failed.
    /// </summary>
    private static void ThrowIfDestinationOccupied(string destFullPath)
    {
        if (File.Exists(destFullPath) || Directory.Exists(destFullPath))
            throw new NameCollisionException(destFullPath);
    }

    /// <summary>
    /// A rename/move whose destination differs from the source only by letter casing is the
    /// SAME entry on case-insensitive NTFS: Exists(dest) would report the source itself and
    /// wrongly flag a collision — but File.Move/Directory.Move apply the re-casing fine.
    /// </summary>
    private static bool IsCaseOnlyDifference(string sourceFullPath, string destFullPath) =>
        string.Equals(sourceFullPath, destFullPath, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(sourceFullPath, destFullPath, StringComparison.Ordinal);

    public void DeleteToRecycleBin(string volGuid, string relativePath)
    {
        var full = Resolve(volGuid, relativePath);
        NativeShell.SendToRecycleBin(full, _logger);
    }

    public void EnsureTargetDirectory(string volGuid, string relativePath)
    {
        var full = Resolve(volGuid, relativePath);
        Directory.CreateDirectory(full);
    }

    public bool Exists(string volGuid, string relativePath)
    {
        var full = Resolve(volGuid, relativePath);
        return File.Exists(full) || Directory.Exists(full);
    }

    public bool IsDirectoryEmpty(string volGuid, string relativePath)
    {
        var full = Resolve(volGuid, relativePath);
        return Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any();
    }

    public bool CanRecycle(string volGuid)
    {
        var mountPoints = GetMountPoints(volGuid);
        if (mountPoints.Count == 0) return false;

        var info = new NativeMethods.SHQUERYRBINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>(),
        };
        return NativeMethods.SHQueryRecycleBin(mountPoints[0], ref info) == 0; // S_OK
    }

    // ── private helpers ─────────────────────────────────────────────────────

    private static string Resolve(string volumeGuid, string relativePath)
    {
        var mountPoints = GetMountPoints(volumeGuid);
        if (mountPoints.Count == 0)
            throw new InvalidOperationException(
                $"Volume '{volumeGuid}' has no mount point (offline?).");

        return ResolveWithinMount(mountPoints[0], relativePath);
    }

    /// <summary>
    /// Composes a volume-relative path against its mount point and guarantees the result
    /// stays inside the mount. The relative path comes from untrusted UI input, so a
    /// drive-qualified path (<c>C:\…</c>), a leading separator (<c>\x</c> — which makes
    /// <see cref="Path.Combine(string, string)"/> discard the mount) or a <c>..</c>
    /// segment must be rejected: this class runs in the elevated service, so an escape
    /// here means writing/deleting outside the catalog volume.
    /// </summary>
    internal static string ResolveWithinMount(string mount, string relativePath)
    {
        relativePath ??= string.Empty;

        // Drive-qualified (contains ':') or rooted (leading '\' or '/') paths would let
        // Path.Combine discard the mount and escape to another drive / the volume root.
        if (relativePath.Contains(':') || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException(
                $"Relative path '{relativePath}' must be relative to the volume root " +
                "(no drive letter, no leading separator).");

        // Any '..' segment can climb out of the volume.
        foreach (var segment in relativePath.Split('\\', '/'))
            if (segment == "..")
                throw new InvalidOperationException(
                    $"Relative path '{relativePath}' must not contain '..' segments.");

        var mountFull = Path.GetFullPath(mount);
        var full = Path.GetFullPath(Path.Combine(mountFull, relativePath));

        // Defense in depth: confirm the resolved path is still under the mount (same
        // trailing-separator containment reused by the smoke harness's guard-rails).
        if (!PathBoundary.IsWithin(mountFull, full))
            throw new InvalidOperationException(
                $"Resolved path '{full}' escapes the volume mount '{mountFull}'.");

        return full;
    }

    private static IReadOnlyList<string> GetMountPoints(string volumeGuid)
    {
        const uint initialSize = 1024;
        var buf = new char[initialSize];

        if (!NativeMethods.GetVolumePathNamesForVolumeName(volumeGuid, buf, initialSize, out var needed))
        {
            if (needed == 0) return [];
            buf = new char[needed];
            if (!NativeMethods.GetVolumePathNamesForVolumeName(volumeGuid, buf, needed, out _))
                return [];
        }

        return VolumePathParsing.SplitDoubleNullTerminated(buf);
    }

    private static void EnsureParent(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null) Directory.CreateDirectory(dir);
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
