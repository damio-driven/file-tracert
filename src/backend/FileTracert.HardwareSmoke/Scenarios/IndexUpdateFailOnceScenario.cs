using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// WP1 (finding #7) — completion with the index update failing once (the SQLITE_BUSY analogue).
/// The completion commit must roll back as a whole: the job re-runs from its checkpoint and ends
/// <c>Completed</c> — never flipped to Failed — and the space fold applies exactly once. The
/// failure is injected by wrapping the REAL <see cref="FileSearchIndex"/> with a first-call-fails
/// decorator; every other service is the production one.
/// </summary>
public sealed class IndexUpdateFailOnceScenario : Scenario
{
    private FailOnceLatch _latch = new();

    public override string Name => "index-update-fail-once";

    public override string Description =>
        "FTS upsert fails once during completion: the job still ends Completed, the space estimate untouched.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override Action<IServiceCollection>? ConfigureServices => services =>
        services.AddScoped<IFileSearchIndex>(sp => new FailOnceFileSearchIndex(
            new FileSearchIndex(sp.GetRequiredService<FileTracertDbContext>()), _latch));

    public override async Task RunAsync(ScenarioContext ctx)
    {
        _latch = new FailOnceLatch(); // fresh per run — the scenario instance is reused across pairs

        // ── arrange ───────────────────────────────────────────────────────────
        const string relative = @"docs\atomic.bin";
        // 64 MB, not a token file: the space assertion below compares the stored estimate with
        // the device inside a tolerance, and the fixture has to be bigger than the noise a real
        // drive produces under other processes for a folded job size to be visible at all.
        var sourceAbsolute = ctx.Source.CreateFile(relative, 64L * 1024 * 1024);
        var expectedHash = ScenarioAssertions.Sha256(sourceAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        var job = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, fileRow.Id), ctx.Ct);

        // The indexing arrange above must not consume the injected failure.
        _latch.Arm();

        // What the device holds just before the job runs — which is what the hard re-check is
        // about to measure and store. Nothing after that point may change the column.
        long freeBeforeRun = LiveFreeBytes(ctx, ctx.Target.Volume);

        // ── act ───────────────────────────────────────────────────────────────
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);
        await ctx.Queue.StopWorkerAsync();

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.True(_latch.HasFired, "the injected index failure must actually have fired");
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"a transient index failure must not end the job Failed ({Harness.QueueDriver.Describe(finished)})");

        var targetAbsolute = Path.Combine(ctx.Target.RootFullPath, "atomic.bin");
        ctx.Assert.FileExists(targetAbsolute, "moved file on the target");
        if (File.Exists(targetAbsolute))
            ctx.Assert.Equal(expectedHash, ScenarioAssertions.Sha256(targetAbsolute), "moved file content");
        ctx.Assert.FileMissing(sourceAbsolute, "source after the completed move");
        AssertNoPartialsAnywhere(ctx);

        // The stored estimate is a measurement, never a running total: the completion — and the
        // retry it went through — must leave it where the re-check's probe put it, i.e. at what
        // the drive held when the job started. A tolerance, because a real drive keeps moving
        // under other processes; a fold applied once (let alone twice) would be off by the job
        // size, far outside it.
        var freeAfter = await ctx.Env.WithDbAsync(db =>
            db.Volumes.Where(v => v.Id == ctx.TargetVolumeId)
                .Select(v => v.FreeBytesLastKnown).SingleAsync(ctx.Ct));
        long tolerance = 16L * 1024 * 1024;
        ctx.Assert.True(
            Math.Abs(freeBeforeRun - freeAfter) <= tolerance,
            $"target FreeBytesLastKnown must hold the figure measured when the job started, not " +
            $"one with the job's {finished.RequiredBytesTarget:N0} B folded into it (device held " +
            $"{freeBeforeRun:N0} B, row says {freeAfter:N0} B)");

        // The index update did land on the successful attempt.
        await AssertCatalogHasFileAsync(ctx, ctx.TargetVolumeId,
            ctx.Target.RelativePath("atomic.bin"), "catalog after the retried completion");
    }

    /// <summary>Armed → the next Upsert throws once; records that it fired.</summary>
    private sealed class FailOnceLatch
    {
        private int _state; // 0 = disarmed, 1 = armed, 2 = fired

        public void Arm() => Interlocked.CompareExchange(ref _state, 1, 0);

        public bool TryFire() => Interlocked.CompareExchange(ref _state, 2, 1) == 1;

        public bool HasFired => Volatile.Read(ref _state) == 2;
    }

    /// <summary>Delegates everything to the real FTS index; the first armed Upsert throws.</summary>
    private sealed class FailOnceFileSearchIndex : IFileSearchIndex
    {
        private readonly IFileSearchIndex _inner;
        private readonly FailOnceLatch _latch;

        public FailOnceFileSearchIndex(IFileSearchIndex inner, FailOnceLatch latch)
        {
            _inner = inner;
            _latch = latch;
        }

        public Task UpsertAsync(int fileId, string name, string path, CancellationToken ct)
        {
            if (_latch.TryFire())
                throw new InvalidOperationException("harness-injected transient index failure (SQLITE_BUSY analogue)");
            return _inner.UpsertAsync(fileId, name, path, ct);
        }

        public Task ClearVolumeAsync(int volumeId, CancellationToken ct) => _inner.ClearVolumeAsync(volumeId, ct);
        public Task SyncVolumeFromDbAsync(int volumeId, CancellationToken ct) => _inner.SyncVolumeFromDbAsync(volumeId, ct);
        public Task RebuildAsync(CancellationToken ct) => _inner.RebuildAsync(ct);
        public Task SyncFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct) => _inner.SyncFilesAsync(fileIds, ct);
        public Task PruneVolumeAsync(int volumeId, CancellationToken ct) => _inner.PruneVolumeAsync(volumeId, ct);
        public Task RemoveAsync(int fileId, CancellationToken ct) => _inner.RemoveAsync(fileId, ct);
        public Task<PagedResult<int>> SearchAsync(FileSearchQuery query, CancellationToken ct) => _inner.SearchAsync(query, ct);
    }
}
