using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Tests.Business;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Tests.Host;

/// <summary>
/// WP2 — the remount can happen while the service is NOT running, and the volume-sync trigger
/// only fires on an offline→online TRANSITION: a process killed between the sync that flipped the
/// volume online and the revaluation that should have followed would leave its parked jobs blocked
/// forever, with the drive sitting there connected. The queue worker therefore re-evaluates the
/// parked jobs once at startup, against the reality it finds.
/// </summary>
public sealed class QueueStartupRevaluationTests
{
    private const string VolumeGuid = @"\\?\Volume{33333333-3333-3333-3333-333333333333}\";

    [Fact]
    public async Task Parked_jobs_are_revaluated_at_startup_without_a_mount_event()
    {
        using var factory = new FileTracertAppFactory
        {
            DisableScan = true,
            // No volume sync: nothing can produce the offline→online event. The only thing that
            // can move this job is the queue worker's own startup pass. The probe still answers
            // for the drive — the hard re-check asks the DEVICE how much room there is, and a
            // volume that does not answer at all would be parked as missing, not as full.
            DisableVolumeSync = true,
            Probe = new FakeVolumesProbe(
            [
                new ProbedVolume(VolumeGuid, null, "Archivio", "NTFS", false, [], 1024 * 1024, 0, null),
            ]),
            Seed = async (db, ct) =>
            {
                // The drive is back (online) but the catalog still holds a job parked on it, and
                // the volume has no room for it: the startup pass must notice both facts.
                var volume = new Volume
                {
                    VolumeGuid = VolumeGuid,
                    FileSystem = "NTFS",
                    ScanEngine = VolumeScanEngine.UsnJournal,
                    IsOnline = true,
                    FreeBytesLastKnown = 1024 * 1024 * 1024,   // the catalog is out of date; the drive is full
                };
                db.Volumes.Add(volume);
                await db.SaveChangesAsync(ct);

                db.OperationJobs.Add(new OperationJob
                {
                    Type = JobType.MoveFile,
                    State = JobState.Blocked,
                    BlockReason = JobBlockReason.TargetVolumeOffline,
                    IsIntraVolume = false,
                    SourceVolumeId = volume.Id,
                    TargetVolumeId = volume.Id,
                    TargetRelativePath = @"Archivio\payload.bin",
                    TotalBytes = 1024 * 1024,
                    RequiredBytesTarget = 1024 * 1024,
                    SequenceOrder = 1,
                    ErrorMessage = "volume scollegato",
                });
                await db.SaveChangesAsync(ct);
            },
        };

        using var _ = factory.CreateClient();

        // The obstacle is no longer the missing volume but the missing space: the reason must
        // follow reality. It stays Blocked, so the worker can never execute it.
        await TestPolling.WaitUntilAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            var job = await db.OperationJobs.AsNoTracking().SingleOrDefaultAsync();
            return job is { BlockReason: JobBlockReason.InsufficientSpace };
        });

        using var read = factory.Services.CreateScope();
        var ctx = read.ServiceProvider.GetRequiredService<FileTracertDbContext>();
        var final = await ctx.OperationJobs.AsNoTracking().SingleAsync();
        final.State.Should().Be(JobState.Blocked, "a job that still does not fit must stay parked");
    }
}
