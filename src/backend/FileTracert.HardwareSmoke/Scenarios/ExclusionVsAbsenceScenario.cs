using FileTracert.Business.Setup;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// Step 11g on real files: the two reasons a row can be missing from a scan are told apart.
///
/// <para>Three files start indexed. Then, between two scans, one folder is made Hidden (still on
/// disk, outside the perimeter), one file is deleted (really gone), and one is left alone. Only
/// the deleted one may come out <c>IsPresent = false</c>; the hidden one must come out
/// <c>IsIncluded = false</c> with its presence untouched, because the difference between "it is
/// not there any more" and "you told me not to look" is the difference between a user hunting for
/// a file that is sitting on their disk and a user reading the truth.</para>
///
/// <para>Then both ways back are exercised on the same fixture: un-hiding the folder and scanning
/// once (the attribute path — nothing in Setup could know), and switching the watched root off and
/// on through the real <see cref="WatchedRootsService"/> with NO scan in between (§4 — re-widening
/// the perimeter must not cost a re-scan).</para>
/// </summary>
public sealed class ExclusionVsAbsenceScenario : Scenario
{
    private const string KeptFile = @"perimeter\keep\keep.dat";
    private const string HiddenFile = @"perimeter\secret\secret.dat";
    private const string DeletedFile = @"perimeter\keep\vanish.dat";
    private const string HiddenFolder = @"perimeter\secret";

    public override string Name => "exclusion-vs-absence";

    public override string Description =>
        "A narrowed perimeter excludes rows without calling them absent, and widening it brings them back.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange: three real files, one real scan ──────────────────────────
        ctx.Source.CreateFile(KeptFile, 16 * 1024);
        ctx.Source.CreateFile(HiddenFile, 16 * 1024);
        var deletedFullPath = ctx.Source.CreateFile(DeletedFile, 8 * 1024);

        await EnsureWatchedRootAsync(ctx, ctx.Source, ctx.SourceVolumeId);
        var firstScan = await ScanVolumeAsync(ctx, ctx.SourceVolumeId);
        ctx.Log($"first scan: {firstScan.TotalSeconds:0.00}s");

        var keptPath = ctx.Source.RelativePath(KeptFile);
        var hiddenPath = ctx.Source.RelativePath(HiddenFile);
        var deletedPath = ctx.Source.RelativePath(DeletedFile);
        var hiddenFolderPath = ctx.Source.RelativePath(HiddenFolder);

        var arranged = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, hiddenPath, "arrange");
        if (arranged is null) return;
        ctx.Assert.True(arranged.IsIncluded, "arrange: the file starts inside the perimeter");

        // ── act: narrow the perimeter AND delete one file, then re-scan ───────
        var folder = new DirectoryInfo(ctx.Source.FullPath(HiddenFolder));
        folder.Attributes |= FileAttributes.Hidden;
        File.Delete(deletedFullPath);

        var reScan = await ScanVolumeAsync(ctx, ctx.SourceVolumeId);
        ctx.Log($"re-scan (one folder hidden, one file deleted): {reScan.TotalSeconds:0.00}s");

        // ── assert: excluded is not absent ────────────────────────────────────
        var hidden = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, hiddenPath, "after the re-scan");
        if (hidden is not null)
        {
            ctx.Assert.True(!hidden.IsIncluded,
                "the file under the hidden folder must be excluded (IsIncluded = false)");
            ctx.Assert.True(hidden.IsPresent,
                "…and must NOT be flagged absent: it is still on disk, the scan simply did not look. " +
                $"On disk right now: {File.Exists(ctx.Source.FullPath(HiddenFile))}");
        }

        var hiddenDir = await FindDirectoryRowAsync(ctx, ctx.SourceVolumeId, hiddenFolderPath);
        if (hiddenDir is not null)
        {
            ctx.Assert.True(hiddenDir.IsPresent,
                "the hidden FOLDER exists on disk, so it must not be flagged absent either");
        }

        var deleted = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, deletedPath, "after the re-scan");
        if (deleted is not null)
        {
            ctx.Assert.True(!deleted.IsPresent, "the file deleted from disk must be flagged absent");
            ctx.Assert.True(deleted.IsIncluded, "…and absence must not be recorded as a filter decision");
        }

        var kept = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, keptPath, "after the re-scan");
        if (kept is not null)
        {
            ctx.Assert.True(kept.IsIncluded && kept.IsPresent, "the untouched file must stay included and present");
        }

        // ── assert: un-hiding costs one scan (no Setup event could know) ──────
        folder.Attributes &= ~FileAttributes.Hidden;
        await ScanVolumeAsync(ctx, ctx.SourceVolumeId);

        var back = await FindFileRowAsync(ctx, ctx.SourceVolumeId, hiddenPath);
        ctx.Assert.True(back is { IsIncluded: true, IsPresent: true },
            "un-hiding the folder and scanning once must bring the row back inside the perimeter; " +
            $"got IsIncluded={back?.IsIncluded}, IsPresent={back?.IsPresent}");

        // ── assert: the root switch reconciles with NO scan at all ────────────
        var rootId = await ctx.Env.WithDbAsync(db => db.WatchedRoots
            .Where(r => r.VolumeId == ctx.SourceVolumeId && r.RelativePath == ctx.Source.RootRelativePath)
            .Select(r => r.Id)
            .FirstAsync(ctx.Ct));

        await UpdateRootAsync(ctx, rootId, isActive: false);

        var afterOff = await FindFileRowAsync(ctx, ctx.SourceVolumeId, keptPath);
        ctx.Assert.True(afterOff is { IsIncluded: false, IsPresent: true },
            "switching the watched root off must exclude its rows without a scan and without touching " +
            $"their presence; got IsIncluded={afterOff?.IsIncluded}, IsPresent={afterOff?.IsPresent}");

        await UpdateRootAsync(ctx, rootId, isActive: true);

        var afterOn = await FindFileRowAsync(ctx, ctx.SourceVolumeId, keptPath);
        ctx.Assert.True(afterOn is { IsIncluded: true, IsPresent: true },
            "switching it back on must re-include them, again without a scan; " +
            $"got IsIncluded={afterOn?.IsIncluded}, IsPresent={afterOn?.IsPresent}");
    }

    /// <summary>Toggles a watched root through the real service, exactly as the API does.</summary>
    private static Task UpdateRootAsync(ScenarioContext ctx, int rootId, bool isActive) =>
        ctx.Env.WithScopeAsync<object?>(async sp =>
        {
            await sp.GetRequiredService<WatchedRootsService>().UpdateAsync(
                rootId, new UpdateWatchedRootRequest(isActive, FilterOverride: null), ctx.Ct);
            return null;
        });
}
