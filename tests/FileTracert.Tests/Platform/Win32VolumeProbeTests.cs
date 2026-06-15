using FileTracert.Business.Volumes;
using FileTracert.Contracts.Enums;
using FileTracert.Platform;
using FileTracert.Platform.Internal;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Platform;

/// <summary>
/// On-machine sanity + contract tests. These exercise real Win32 interop, so
/// they only make sense on Windows and are tagged Category=Platform so they can
/// be filtered out elsewhere (<c>dotnet test --filter Category!=Platform</c>).
/// </summary>
[Trait("Category", "Platform")]
public class Win32VolumeProbeTests
{
    private static Win32VolumeProbe CreateProbe() =>
        new(
            new WmiPhysicalDiskResolver(NullLogger<WmiPhysicalDiskResolver>.Instance),
            NullLogger<Win32VolumeProbe>.Instance);

    [Fact]
    public void EnumerateVolumes_returns_at_least_one_volume()
    {
        var volumes = CreateProbe().EnumerateVolumes();

        volumes.Should().NotBeEmpty();
        volumes.Should().OnlyContain(v => VolumePathParsing.IsVolumeGuidPath(v.VolumeGuid));
    }

    [Fact]
    public void EnumerateVolumes_system_volume_has_letter_and_filesystem()
    {
        var volumes = CreateProbe().EnumerateVolumes();

        var systemVolume = volumes.FirstOrDefault(v =>
            v.MountPoints.Any(m => string.Equals(m, @"C:\", StringComparison.OrdinalIgnoreCase)));

        systemVolume.Should().NotBeNull("the C:\\ volume must be discoverable");
        systemVolume!.FileSystem.Should().NotBeNullOrEmpty();
        systemVolume.CapacityBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void System_volume_reports_physical_extents_and_classifies_as_a_real_disk()
    {
        var volumes = CreateProbe().EnumerateVolumes();

        var systemVolume = volumes.FirstOrDefault(v =>
            v.MountPoints.Any(m => string.Equals(m, @"C:\", StringComparison.OrdinalIgnoreCase)));

        systemVolume.Should().NotBeNull();
        // The live IOCTL must see real disk extents for C:\ — otherwise the classifier
        // would wrongly hide the system disk as a cloud mount.
        systemVolume!.HasPhysicalExtents.Should().BeTrue();
        VolumeClassifier.Classify(systemVolume).Should().BeOneOf(VolumeKind.Fixed, VolumeKind.System);
    }

    [Fact]
    public void TryGetByGuid_roundtrips_an_enumerated_volume()
    {
        var probe = CreateProbe();
        var first = probe.EnumerateVolumes().First();

        var found = probe.TryGetByGuid(first.VolumeGuid);

        found.Should().NotBeNull();
        found!.VolumeGuid.Should().Be(first.VolumeGuid);
    }

    [Fact]
    public void TryGetByGuid_returns_null_for_unknown_guid()
    {
        var probe = CreateProbe();

        probe.TryGetByGuid(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\")
            .Should().BeNull();
    }

    [Fact]
    public void TryGetByGuid_returns_null_for_malformed_input()
    {
        CreateProbe().TryGetByGuid("not-a-volume-guid").Should().BeNull();
    }
}
