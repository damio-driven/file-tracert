using FileTracert.Contracts.Platform;
using FileTracert.Platform;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Platform;

/// <summary>
/// On-machine integration tests for Win32FileMover. All tests run against
/// real temp directories on the local disk.
/// Tag Category=Platform so CI can filter them: <c>dotnet test --filter Category!=Platform</c>.
/// </summary>
[Trait("Category", "Platform")]
public class Win32FileMoverTests : IDisposable
{
    private readonly Win32FileMover _mover;
    private readonly string _volumeGuid;
    private readonly string _mountPoint;   // e.g. "C:\"
    private readonly string _relRoot;      // e.g. "Users\...\Temp\ft-test-xxx"
    private readonly string _absRoot;      // absolute path of the test sandbox

    public Win32FileMoverTests()
    {
        _mover = new Win32FileMover(NullLogger<Win32FileMover>.Instance);

        var probe = new Win32VolumeProbe(
            new WmiPhysicalDiskResolver(NullLogger<WmiPhysicalDiskResolver>.Instance),
            NullLogger<Win32VolumeProbe>.Instance);

        // Pick the volume that hosts the system temp folder.
        var tempPath = Path.GetTempPath();
        var vol = probe.EnumerateVolumes()
            .Where(v => v.MountPoints.Count > 0)
            .OrderByDescending(v => v.MountPoints[0].Length) // longest prefix wins
            .First(v => tempPath.StartsWith(v.MountPoints[0], StringComparison.OrdinalIgnoreCase));

        _volumeGuid = vol.VolumeGuid;
        _mountPoint = vol.MountPoints[0]; // e.g. "C:\"

        _absRoot = Path.Combine(tempPath, $"ft-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_absRoot);

        // Strip the mount point to get a path relative to the volume root.
        _relRoot = Path.GetRelativePath(_mountPoint, _absRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_absRoot))
            Directory.Delete(_absRoot, recursive: true);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Relative path inside the test sandbox.</summary>
    private string R(params string[] parts) =>
        Path.Combine([_relRoot, ..parts]);

    private string Abs(string rel) =>
        Path.GetFullPath(Path.Combine(_mountPoint, rel));

    private void WriteFile(string relPath, string content = "hello")
    {
        var abs = Abs(relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    // ── CreateFolder ─────────────────────────────────────────────────────────

    [Fact]
    public void CreateFolder_creates_directory_and_intermediates()
    {
        var rel = R("a", "b", "c");

        _mover.CreateFolder(_volumeGuid, rel);

        Directory.Exists(Abs(rel)).Should().BeTrue();
    }

    [Fact]
    public void CreateFolder_is_idempotent_when_dir_already_exists()
    {
        var rel = R("exists");
        Directory.CreateDirectory(Abs(rel));

        var act = () => _mover.CreateFolder(_volumeGuid, rel);

        act.Should().NotThrow();
    }

    // ── RenameIntraVolume ────────────────────────────────────────────────────

    [Fact]
    public void RenameIntraVolume_renames_file()
    {
        var srcRel = R("old.txt");
        var newName = "new.txt";
        WriteFile(srcRel);

        _mover.RenameIntraVolume(_volumeGuid, srcRel, newName);

        File.Exists(Abs(srcRel)).Should().BeFalse();
        File.Exists(Abs(R("new.txt"))).Should().BeTrue();
    }

    [Fact]
    public void RenameIntraVolume_renames_directory()
    {
        var srcRel = R("dir-old");
        Directory.CreateDirectory(Abs(srcRel));
        WriteFile(R("dir-old", "child.txt"));

        _mover.RenameIntraVolume(_volumeGuid, srcRel, "dir-new");

        Directory.Exists(Abs(srcRel)).Should().BeFalse();
        Directory.Exists(Abs(R("dir-new"))).Should().BeTrue();
        File.Exists(Abs(R("dir-new", "child.txt"))).Should().BeTrue();
    }

    // ── MoveIntraVolume ──────────────────────────────────────────────────────

    [Fact]
    public void MoveIntraVolume_moves_file_to_new_location()
    {
        var srcRel = R("src", "file.txt");
        var destRel = R("dst", "file.txt");
        WriteFile(srcRel, "move-me");

        _mover.MoveIntraVolume(_volumeGuid, srcRel, destRel);

        File.Exists(Abs(srcRel)).Should().BeFalse();
        File.Exists(Abs(destRel)).Should().BeTrue();
        File.ReadAllText(Abs(destRel)).Should().Be("move-me");
    }

    [Fact]
    public void MoveIntraVolume_creates_missing_destination_directory()
    {
        var srcRel = R("to-move.txt");
        var destRel = R("new-dir", "to-move.txt");
        WriteFile(srcRel);

        _mover.MoveIntraVolume(_volumeGuid, srcRel, destRel);

        Directory.Exists(Abs(R("new-dir"))).Should().BeTrue();
        File.Exists(Abs(destRel)).Should().BeTrue();
    }

    [Fact]
    public void MoveIntraVolume_moves_directory_with_children()
    {
        var srcRel = R("srcdir");
        var destRel = R("targetdir");
        Directory.CreateDirectory(Abs(srcRel));
        WriteFile(R("srcdir", "a.txt"), "aaaa");
        WriteFile(R("srcdir", "sub", "b.txt"), "bbbb");

        _mover.MoveIntraVolume(_volumeGuid, srcRel, destRel);

        Directory.Exists(Abs(srcRel)).Should().BeFalse();
        File.ReadAllText(Abs(R("targetdir", "a.txt"))).Should().Be("aaaa");
        File.ReadAllText(Abs(R("targetdir", "sub", "b.txt"))).Should().Be("bbbb");
    }

    // ── CopyFileAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyFileAsync_copies_content_to_partial_path()
    {
        var srcRel = R("orig.txt");
        var dstPartial = R("dest", "copy.fadit-partial");
        WriteFile(srcRel, "copy content");

        long lastProgress = 0;
        Task OnProgress(long b, CancellationToken _) { lastProgress = b; return Task.CompletedTask; }

        await _mover.CopyFileAsync(_volumeGuid, srcRel, _volumeGuid, dstPartial, OnProgress, CancellationToken.None);

        File.Exists(Abs(dstPartial)).Should().BeTrue();
        File.ReadAllText(Abs(dstPartial)).Should().Be("copy content");
    }

    [Fact]
    public async Task CopyFileAsync_reports_progress()
    {
        var srcRel = R("big.bin");
        var dstPartial = R("big.fadit-partial");
        // Write 200 KB so at least one progress tick fires
        File.WriteAllBytes(Abs(srcRel), new byte[200 * 1024]);

        var progressValues = new List<long>();
        Task OnProgress(long b, CancellationToken _) { progressValues.Add(b); return Task.CompletedTask; }

        await _mover.CopyFileAsync(_volumeGuid, srcRel, _volumeGuid, dstPartial, OnProgress, CancellationToken.None);

        // onProgress is awaited inline (not posted to a thread pool like IProgress<T>), so every
        // tick is already applied by the time CopyFileAsync returns — no flush delay needed.
        progressValues.Should().NotBeEmpty();
        progressValues.Last().Should().Be(200 * 1024);
    }

    [Fact]
    public async Task CopyFileAsync_cancelled_leaves_partial_on_disk()
    {
        var srcRel = R("src-cancel.bin");
        var dstPartial = R("dst-cancel.fadit-partial");
        // Large enough that cancellation fires mid-copy
        File.WriteAllBytes(Abs(srcRel), new byte[2 * 1024 * 1024]);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));

        var act = async () =>
            await _mover.CopyFileAsync(_volumeGuid, srcRel, _volumeGuid, dstPartial, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        // Partial may or may not exist depending on timing; the key invariant is no exception other than OCE.
        // If the partial exists, it must not be a complete copy.
        if (File.Exists(Abs(dstPartial)))
            new FileInfo(Abs(dstPartial)).Length.Should().BeLessThan(2 * 1024 * 1024);
    }

    // ── Verify ───────────────────────────────────────────────────────────────

    [Fact]
    public void Verify_returns_true_for_identical_files_size_only()
    {
        WriteFile(R("va.txt"), "same");
        WriteFile(R("vb.txt"), "same");

        _mover.Verify(_volumeGuid, R("va.txt"), _volumeGuid, R("vb.txt"), withHash: false)
            .Should().BeTrue();
    }

    [Fact]
    public void Verify_returns_false_for_different_size()
    {
        WriteFile(R("va2.txt"), "short");
        WriteFile(R("vb2.txt"), "longer content");

        _mover.Verify(_volumeGuid, R("va2.txt"), _volumeGuid, R("vb2.txt"), withHash: false)
            .Should().BeFalse();
    }

    [Fact]
    public void Verify_returns_true_with_hash_for_identical_content()
    {
        var content = "hash-me-please";
        WriteFile(R("ha.txt"), content);
        WriteFile(R("hb.txt"), content);

        _mover.Verify(_volumeGuid, R("ha.txt"), _volumeGuid, R("hb.txt"), withHash: true)
            .Should().BeTrue();
    }

    [Fact]
    public void Verify_returns_false_with_hash_for_same_size_different_content()
    {
        // Same length, different content — size-only would pass but hash must catch it.
        WriteFile(R("xa.txt"), "AAAAAA");
        WriteFile(R("xb.txt"), "BBBBBB");

        _mover.Verify(_volumeGuid, R("xa.txt"), _volumeGuid, R("xb.txt"), withHash: true)
            .Should().BeFalse();
    }

    [Fact]
    public void Verify_returns_false_when_file_missing()
    {
        WriteFile(R("present.txt"), "here");

        _mover.Verify(_volumeGuid, R("present.txt"), _volumeGuid, R("ghost.txt"), withHash: false)
            .Should().BeFalse();
    }

    // ── FinalizePartial ──────────────────────────────────────────────────────

    [Fact]
    public void FinalizePartial_renames_partial_to_final()
    {
        var partial = R("file.fadit-partial");
        var final = R("file.txt");
        WriteFile(partial, "done");

        _mover.FinalizePartial(_volumeGuid, partial, final);

        File.Exists(Abs(partial)).Should().BeFalse();
        File.Exists(Abs(final)).Should().BeTrue();
        File.ReadAllText(Abs(final)).Should().Be("done");
    }

    [Fact]
    public void FinalizePartial_throws_NameCollisionException_when_target_exists()
    {
        var partial = R("col.fadit-partial");
        var final = R("col.txt");
        WriteFile(partial, "partial");
        WriteFile(final, "existing");

        var act = () => _mover.FinalizePartial(_volumeGuid, partial, final);

        act.Should().Throw<NameCollisionException>()
            .Which.TargetPath.Should().EndWith("col.txt");
    }

    // ── DeleteToRecycleBin ───────────────────────────────────────────────────

    [Fact]
    public void DeleteToRecycleBin_removes_file_from_original_location()
    {
        var rel = R("to-delete.txt");
        WriteFile(rel, "goodbye");

        _mover.DeleteToRecycleBin(_volumeGuid, rel);

        File.Exists(Abs(rel)).Should().BeFalse();
    }

    [Fact]
    public void DeleteToRecycleBin_removes_directory_from_original_location()
    {
        var rel = R("dir-to-delete");
        Directory.CreateDirectory(Abs(rel));
        WriteFile(R("dir-to-delete", "inner.txt"), "x");

        _mover.DeleteToRecycleBin(_volumeGuid, rel);

        Directory.Exists(Abs(rel)).Should().BeFalse();
    }

    // ── EnsureTargetDirectory ────────────────────────────────────────────────

    [Fact]
    public void EnsureTargetDirectory_creates_nested_directories()
    {
        var rel = R("p1", "p2", "p3");

        _mover.EnsureTargetDirectory(_volumeGuid, rel);

        Directory.Exists(Abs(rel)).Should().BeTrue();
    }

    // ── IsDirectoryEmpty / CanRecycle ────────────────────────────────────────

    [Fact]
    public void IsDirectoryEmpty_true_for_empty_directory()
    {
        var rel = R("empty-dir");
        Directory.CreateDirectory(Abs(rel));

        _mover.IsDirectoryEmpty(_volumeGuid, rel).Should().BeTrue();
    }

    [Fact]
    public void IsDirectoryEmpty_false_when_directory_contains_a_file()
    {
        var rel = R("full-dir");
        WriteFile(R("full-dir", "content.txt"));

        _mover.IsDirectoryEmpty(_volumeGuid, rel).Should().BeFalse();
    }

    [Fact]
    public void IsDirectoryEmpty_false_when_directory_contains_only_a_subdirectory()
    {
        var rel = R("nested-dir");
        Directory.CreateDirectory(Abs(R("nested-dir", "child")));

        _mover.IsDirectoryEmpty(_volumeGuid, rel).Should().BeFalse();
    }

    [Fact]
    public void IsDirectoryEmpty_false_for_missing_directory()
    {
        _mover.IsDirectoryEmpty(_volumeGuid, R("does-not-exist")).Should().BeFalse();
    }

    [Fact]
    public void CanRecycle_true_on_the_system_volume()
    {
        // The volume hosting %TEMP% always has a recycle bin.
        _mover.CanRecycle(_volumeGuid).Should().BeTrue();
    }

    [Fact]
    public void CanRecycle_false_for_volume_with_no_mount_point()
    {
        const string fakeGuid = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\";

        _mover.CanRecycle(fakeGuid).Should().BeFalse();
    }

    // ── offline volume ───────────────────────────────────────────────────────

    [Fact]
    public void Resolve_throws_for_volume_with_no_mount_point()
    {
        const string fakeGuid = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\";

        var act = () => _mover.CreateFolder(fakeGuid, "any");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no mount point*");
    }
}
