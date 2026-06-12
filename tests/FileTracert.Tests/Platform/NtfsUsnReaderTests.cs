using System.Security.Principal;
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
}
