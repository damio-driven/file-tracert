using System.Security.Principal;
using FileTracert.Contracts.Platform;
using FileTracert.Platform;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Platform;

/// <summary>
/// On-machine tests against the real C: journal. Require Windows + elevation
/// (opening the volume handle and reading the MFT need admin), so they are
/// tagged Category=Platform and excluded elsewhere with
/// <c>--filter Category!=Platform</c>. xUnit v2 has no dynamic skip, so when the
/// preconditions are not met the test returns early (vacuous pass).
/// </summary>
[Trait("Category", "Platform")]
public class NtfsUsnReaderTests
{
    private static NtfsUsnReader CreateReader() => new(NullLogger<NtfsUsnReader>.Instance);

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string? TryGetSystemVolumeGuid()
    {
        var probe = new Win32VolumeProbe(
            new WmiPhysicalDiskResolver(NullLogger<WmiPhysicalDiskResolver>.Instance),
            NullLogger<Win32VolumeProbe>.Instance);

        return probe.EnumerateVolumes()
            .FirstOrDefault(v => v.MountPoints.Any(m =>
                string.Equals(m, @"C:\", StringComparison.OrdinalIgnoreCase)))
            ?.VolumeGuid;
    }

    [Fact]
    public void SupportsUsn_is_true_for_the_ntfs_system_volume()
    {
        if (!IsElevated() || TryGetSystemVolumeGuid() is not { } guid)
        {
            return;
        }

        CreateReader().SupportsUsn(guid).Should().BeTrue();
    }

    [Fact]
    public void GetJournalState_reports_a_positive_next_usn()
    {
        if (!IsElevated() || TryGetSystemVolumeGuid() is not { } guid)
        {
            return;
        }

        var state = CreateReader().GetJournalState(guid);

        state.JournalId.Should().NotBe(0ul);
        state.NextUsn.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ReadFullSnapshot_reconstructs_known_paths()
    {
        if (!IsElevated() || TryGetSystemVolumeGuid() is not { } guid)
        {
            return;
        }

        var entries = CreateReader().ReadFullSnapshot(guid, CancellationToken.None).ToList();

        entries.Should().HaveCountGreaterThan(1000);
        entries.Should().OnlyContain(e => e.SizeBytes == null); // USN has no size
        entries.Should().Contain(e =>
            e.IsDirectory && string.Equals(e.RelativePath, "Windows", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadChanges_from_current_tail_is_empty_without_rescan()
    {
        if (!IsElevated() || TryGetSystemVolumeGuid() is not { } guid)
        {
            return;
        }

        var reader = CreateReader();
        var state = reader.GetJournalState(guid);

        var result = reader.ReadChanges(guid, state.NextUsn, state.JournalId, CancellationToken.None);

        result.RequiresFullRescan.Should().BeFalse();
        result.Changes.Should().BeEmpty();
        result.NextUsn.Should().BeGreaterThanOrEqualTo(state.NextUsn);
    }

    /// <summary>
    /// The promise of CLAUDE.md §1.2, asserted for the first time: work done <em>outside</em> the
    /// application shows up in the delta, with the reason that says what happened. Everything the
    /// test does to the filesystem is done through plain BCL calls in the user's temp folder - no
    /// product code is involved in producing the changes, which is the whole point.
    /// <para>
    /// The cursor is taken before the work and the read is done after it, so this also fixes the
    /// direction the sibling test cannot: reading from the current tail proves nothing arrives,
    /// this proves what was done in between does.
    /// </para>
    /// </summary>
    [Fact]
    public void ReadChanges_sees_work_done_outside_the_application()
    {
        if (!IsElevated() || TryGetSystemVolumeGuid() is not { } guid)
        {
            return;
        }

        var reader = CreateReader();
        var before = reader.GetJournalState(guid);

        // Unique names: the journal is volume-wide and C: is busy, so the assertions have to name
        // files that can only be ours.
        var stamp = Guid.NewGuid().ToString("N");
        var createdName = $"ft-usn-{stamp}-created.bin";
        var renamedName = $"ft-usn-{stamp}-renamed.bin";
        var directory = Path.Combine(Path.GetTempPath(), $"ft-usn-{stamp}");
        Directory.CreateDirectory(directory);
        try
        {
            var created = Path.Combine(directory, createdName);
            File.WriteAllBytes(created, new byte[64]);
            File.Move(created, Path.Combine(directory, renamedName));
            File.Delete(Path.Combine(directory, renamedName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        var result = reader.ReadChanges(guid, before.NextUsn, before.JournalId, CancellationToken.None);

        result.RequiresFullRescan.Should().BeFalse();
        result.NextUsn.Should().BeGreaterThan(before.NextUsn);

        result.Changes.Should().Contain(
            c => c.Entry.Name == createdName && (c.Reason & UsnReason.FileCreate) != 0,
            "the create must be in the delta");
        result.Changes.Should().Contain(
            c => c.IsRename && c.OldName == createdName,
            "the rename must carry the name the file had before it");
        result.Changes.Should().Contain(
            c => c.Entry.Name == renamedName && (c.Reason & UsnReason.FileDelete) != 0,
            "the delete must be in the delta, under the name the file had when it was deleted");
    }

    [Fact]
    public void ReadChanges_with_wrong_journal_id_requires_full_rescan()
    {
        if (!IsElevated() || TryGetSystemVolumeGuid() is not { } guid)
        {
            return;
        }

        var reader = CreateReader();
        var state = reader.GetJournalState(guid);

        var result = reader.ReadChanges(guid, state.FirstUsn, journalId: 0xDEADBEEF, CancellationToken.None);

        result.RequiresFullRescan.Should().BeTrue();
    }

    /// <summary>
    /// The second way a cursor dies, and the one an id comparison cannot catch: the journal is
    /// still the same instance, but it has wrapped past where we stopped reading. Everything
    /// between our position and <c>LowestValidUsn</c> is gone, so a delta from there would be
    /// silently incomplete — the caller has to be told to rescan, not handed the surviving tail.
    /// </summary>
    [Fact]
    public void ReadChanges_from_below_the_lowest_valid_usn_requires_full_rescan()
    {
        if (!IsElevated() || TryGetSystemVolumeGuid() is not { } guid)
        {
            return;
        }

        var reader = CreateReader();
        var state = reader.GetJournalState(guid);
        if (state.LowestValidUsn <= 0)
        {
            // A journal that has never trimmed has nothing below its floor to ask for.
            return;
        }

        var result = reader.ReadChanges(
            guid, sinceUsn: state.LowestValidUsn - 1, state.JournalId, CancellationToken.None);

        result.RequiresFullRescan.Should().BeTrue();
        result.Changes.Should().BeEmpty("a delta that cannot be trusted must not be handed over at all");
        result.NextUsn.Should().Be(state.NextUsn, "the caller still needs a cursor to restart from");
    }
}
