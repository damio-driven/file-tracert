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

        if (Directory.Exists(full))
            Directory.Move(full, dest);
        else
            File.Move(full, dest);
    }

    public void MoveIntraVolume(string volumeGuid, string srcRel, string destRel)
    {
        var src = Resolve(volumeGuid, srcRel);
        var dest = Resolve(volumeGuid, destRel);
        EnsureParent(dest);

        if (Directory.Exists(src))
            Directory.Move(src, dest);
        else
            File.Move(src, dest);
    }

    public async Task CopyFileAsync(string srcVolGuid, string srcRel,
                                     string destVolGuid, string destPartialRel,
                                     IProgress<long>? progress, CancellationToken ct)
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
            progress?.Report(totalCopied);
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

        if (File.Exists(final))
            throw new NameCollisionException(final);

        EnsureParent(final);
        File.Move(partial, final);
    }

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

    // ── private helpers ─────────────────────────────────────────────────────

    private static string Resolve(string volumeGuid, string relativePath)
    {
        var mountPoints = GetMountPoints(volumeGuid);
        if (mountPoints.Count == 0)
            throw new InvalidOperationException(
                $"Volume '{volumeGuid}' has no mount point (offline?).");

        return Path.GetFullPath(Path.Combine(mountPoints[0], relativePath));
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
