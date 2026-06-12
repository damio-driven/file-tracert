using FileTracert.Business.Dashboard;
using FluentAssertions;

namespace FileTracert.Tests.Business;

public sealed class DashboardStatsAssemblerTests
{
    [Fact]
    public void Passes_totals_through_and_zeroes_queue_placeholders()
    {
        var dto = DashboardStatsAssembler.From(totalFiles: 1_214_882, totalBytes: 3_400_000_000_000, volumesOnline: 3, volumesTotal: 4);

        dto.TotalFiles.Should().Be(1_214_882);
        dto.TotalBytes.Should().Be(3_400_000_000_000);
        dto.VolumesOnline.Should().Be(3);
        dto.VolumesTotal.Should().Be(4);

        // Queue subsystem doesn't exist yet (step 8) — always zero for now.
        dto.QueuedJobs.Should().Be(0);
        dto.BlockedJobs.Should().Be(0);
        dto.RunningJobs.Should().Be(0);
        dto.PendingBytes.Should().Be(0);
    }
}
