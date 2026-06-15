using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FluentAssertions;

namespace FileTracert.Tests.Business;

public sealed class ScanStatusTrackerTests
{
    [Fact]
    public void Snapshot_is_empty_when_no_scan_is_active()
    {
        new ScanStatusTracker().Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Begin_then_report_tracks_phase_and_counts()
    {
        var tracker = new ScanStatusTracker();

        tracker.Begin(7, "Data");
        tracker.ReportSeen(7, 142_000, currentRoot: "Photos");
        tracker.SetPhase(7, ScanPhase.Writing);
        tracker.ReportWritten(7, 80_000);

        var status = tracker.Snapshot().Should().ContainSingle().Subject;
        status.VolumeId.Should().Be(7);
        status.Label.Should().Be("Data");
        status.Phase.Should().Be(ScanPhase.Writing);
        status.ItemsSeen.Should().Be(142_000);
        status.ItemsWritten.Should().Be(80_000);
        status.CurrentRoot.Should().Be("Photos");
        status.UpdatedUtc.Should().BeOnOrAfter(status.StartedUtc);
    }

    [Fact]
    public void Begin_starts_in_enumerating_phase()
    {
        var tracker = new ScanStatusTracker();
        tracker.Begin(1, null);

        tracker.Snapshot().Single().Phase.Should().Be(ScanPhase.Enumerating);
    }

    [Fact]
    public void Complete_removes_the_entry()
    {
        var tracker = new ScanStatusTracker();
        tracker.Begin(1, "A");
        tracker.Complete(1);

        tracker.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Fail_removes_the_entry()
    {
        var tracker = new ScanStatusTracker();
        tracker.Begin(1, "A");
        tracker.Fail(1);

        tracker.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Tracks_multiple_volumes_independently()
    {
        var tracker = new ScanStatusTracker();
        tracker.Begin(1, "A");
        tracker.Begin(2, "B");
        tracker.ReportSeen(2, 50);

        var ids = tracker.Snapshot().Select(s => s.VolumeId).ToList();
        ids.Should().BeEquivalentTo([1, 2]);
        tracker.Snapshot().Single(s => s.VolumeId == 2).ItemsSeen.Should().Be(50);
    }

    [Fact]
    public void Updates_to_an_untracked_volume_are_ignored()
    {
        var tracker = new ScanStatusTracker();

        // No Begin → these must be no-ops, not exceptions, and must not create an entry.
        tracker.ReportSeen(99, 10);
        tracker.SetPhase(99, ScanPhase.Writing);

        tracker.Snapshot().Should().BeEmpty();
    }
}
