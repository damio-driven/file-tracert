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
/// <para>Then step 11h, on the same fixture: the type filter is narrowed and widened again through
/// the real <see cref="FilterSettingsService"/>, and the file under the hidden folder must NOT come
/// back with it — a wider allow-list says nothing about a folder the scan was told to skip — while
/// the file the allow-list itself excluded must, and must be findable in Search again, with no scan
/// in between.</para>
///
/// <para>Then both ways back are exercised on the same fixture: un-hiding the folder and scanning
/// once (the attribute path — nothing in Setup could know), and switching the watched root off and
/// on through the real <see cref="WatchedRootsService"/> with NO scan in between (§4 — re-widening
/// the perimeter must not cost a re-scan).</para>
///
/// <para>Then step 16, the half A2 names, on the same real files: a segment added to
/// <c>ExcludedPaths</c> has to exclude the rows the catalog ALREADY holds under it — with
/// <c>IsPresent</c> untouched, because a perimeter decision is not an absence (§6) — and dropping
/// it has to re-admit them, both with no scan at all. Before this step neither happened: adding a
/// segment excluded nothing, the screen reported a reconciliation and <c>NeedsScan = false</c>, and
/// the rows stayed navigable and findable until some full scan happened to pass. That is an
/// exclusion silently not applied, which is the worst shape of failure available here, because the
/// user believes they decided something.</para>
///
/// <para>Deliberately the LAST act of the scenario, and on a folder of its own: it is the only one
/// that writes a GLOBAL setting whose scope is every root on every volume, so running it last keeps
/// the earlier assertions reading a perimeter narrowed only by the things they are about, and
/// dropping the segment again leaves the throwaway catalog in the state it started in.</para>
/// </summary>
public sealed class ExclusionVsAbsenceScenario : Scenario
{
    private const string KeptFile = @"perimeter\keep\keep.dat";
    private const string HiddenFile = @"perimeter\secret\secret.dat";
    private const string DeletedFile = @"perimeter\keep\vanish.dat";
    private const string HiddenFolder = @"perimeter\secret";

    /// <summary>The type-filtered one: excluded by the allow-list, so reconciliation owns it.</summary>
    private const string TypedFile = @"perimeter\keep\ledger.log";

    /// <summary>
    /// The path-segment one (step 16): it sits under a folder whose NAME the user excludes, so the
    /// reconciler owns it too — the segment is in the row's own <c>MaterializedPath</c> and no disk
    /// read is involved. Its own folder, not <c>keep</c>, so excluding the segment cannot be
    /// confused with excluding the rest of the fixture.
    /// </summary>
    private const string SegmentFile = @"perimeter\vault\books.dat";

    private const string SegmentFolder = @"perimeter\vault";

    /// <summary>
    /// The segment itself. It must not appear anywhere in the volume-relative path ABOVE the
    /// fixture either — <c>FileFilter.IsPathExcluded</c> splits the file's whole volume-relative
    /// path, so a segment matching one of the harness's own scratch folders would exclude the
    /// entire area and make every assertion below pass for the wrong reason.
    /// </summary>
    private const string ExcludedSegment = "vault";

    public override string Name => "exclusion-vs-absence";

    public override string Description =>
        "A narrowed perimeter excludes rows without calling them absent, and widening it brings them back.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange: three real files, one real scan ──────────────────────────
        ctx.Source.CreateFile(KeptFile, 16 * 1024);
        ctx.Source.CreateFile(HiddenFile, 16 * 1024);
        ctx.Source.CreateFile(TypedFile, 4 * 1024);
        ctx.Source.CreateFile(SegmentFile, 4 * 1024);
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

        // ── act: narrow the perimeter AND the type filter, delete one file, re-scan ──
        var folder = new DirectoryInfo(ctx.Source.FullPath(HiddenFolder));
        folder.Attributes |= FileAttributes.Hidden;
        File.Delete(deletedFullPath);

        // Only .dat from here on: ledger.log leaves the index by TYPE, which is the cause
        // reconciliation owns. The scan that follows prunes its search entry.
        await SetAllowedExtensionsAsync(ctx, "dat");

        var reScan = await ScanVolumeAsync(ctx, ctx.SourceVolumeId);
        ctx.Log($"re-scan (one folder hidden, one file deleted, types narrowed): {reScan.TotalSeconds:0.00}s");

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

        // ── assert: widening the TYPE filter undoes only the type exclusion (11h) ──
        var typedPath = ctx.Source.RelativePath(TypedFile);
        var narrowed = await FindFileRowAsync(ctx, ctx.SourceVolumeId, typedPath);
        ctx.Assert.True(narrowed is { IsIncluded: false },
            $"arrange: the allow-list must have excluded ledger.log; got IsIncluded={narrowed?.IsIncluded}");
        ctx.Assert.True((await SearchByNameAsync(ctx, "ledger")).Count == 0,
            "arrange: an excluded file must not be a search hit");

        await SetAllowedExtensionsAsync(ctx); // every type again — and NO scan after it

        var stillHidden = await FindFileRowAsync(ctx, ctx.SourceVolumeId, hiddenPath);
        ctx.Assert.True(stillHidden is { IsIncluded: false, IsPresent: true },
            "a wider type filter says nothing about a folder the scan was told to skip: the file " +
            "under the hidden folder must stay excluded. " +
            $"Got IsIncluded={stillHidden?.IsIncluded}, IsPresent={stillHidden?.IsPresent}; " +
            $"hidden on disk right now: {folder.Attributes.HasFlag(FileAttributes.Hidden)}");

        var widened = await FindFileRowAsync(ctx, ctx.SourceVolumeId, typedPath);
        ctx.Assert.True(widened is { IsIncluded: true, IsPresent: true },
            "…while the file the ALLOW-LIST excluded must come back, with no scan (§4); " +
            $"got IsIncluded={widened?.IsIncluded}, IsPresent={widened?.IsPresent}");

        var foundAgain = await SearchByNameAsync(ctx, "ledger");
        ctx.Assert.True(foundAgain.Count == 1 && widened is not null && foundAgain[0] == widened.Id,
            "…and Search must agree with the Catalog without waiting for a scan: the reconciliation " +
            $"has to put the pruned FTS entry back. Got {foundAgain.Count} hit(s).");

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

        // ── assert: an excluded path SEGMENT reaches the rows already catalogued (step 16 / A2) ──
        var segmentPath = ctx.Source.RelativePath(SegmentFile);
        var segmentFolderPath = ctx.Source.RelativePath(SegmentFolder);

        var beforeSegment = await AssertCatalogHasFileAsync(
            ctx, ctx.SourceVolumeId, segmentPath, "before the segment is excluded");
        if (beforeSegment is not null)
        {
            ctx.Assert.True(
                beforeSegment is { IsIncluded: true, IsPresent: true, ExcludedByPath: false },
                "arrange: the file under 'vault' starts inside the perimeter; got " +
                $"IsIncluded={beforeSegment.IsIncluded}, IsPresent={beforeSegment.IsPresent}, " +
                $"ExcludedByPath={beforeSegment.ExcludedByPath}");
            ctx.Assert.True(
                (await SearchByNameAsync(ctx, "books")).Contains(beforeSegment.Id),
                "arrange: …and is findable in Search, which is what makes its disappearance below mean something");
        }

        var segmentNarrowed = await SetExcludedPathsAsync(ctx, ExcludedSegment);
        ctx.Log(
            $"excluded segment '{ExcludedSegment}' added: included={segmentNarrowed.IncludedCount} " +
            $"excluded={segmentNarrowed.ExcludedCount} needsScan={segmentNarrowed.NeedsScan}");

        var excludedBySegment = await FindFileRowAsync(ctx, ctx.SourceVolumeId, segmentPath);
        ctx.Assert.True(
            excludedBySegment is { IsIncluded: false, ExcludedByPath: true },
            "a segment added to ExcludedPaths must exclude the rows the catalog ALREADY holds under " +
            "it, with no scan — this is the defect step 16 exists to close, and until it the answer " +
            "here was 'nothing happened while the screen said it had'. Got " +
            $"IsIncluded={excludedBySegment?.IsIncluded}, ExcludedByPath={excludedBySegment?.ExcludedByPath}");
        ctx.Assert.True(
            excludedBySegment is { IsPresent: true },
            "…and IsPresent must be left strictly alone: the file is on disk and nobody looked for it, " +
            "which is the whole distinction this scenario is named after (§6). On disk right now: " +
            $"{File.Exists(ctx.Source.FullPath(SegmentFile))}");
        ctx.Assert.True(
            excludedBySegment is { ExcludedByScan: false },
            "…and the cause recorded must be the one the SETTINGS own. ExcludedByScan is undone by " +
            "nothing short of another scan, so writing it for a segment would pin the row out past " +
            $"the moment the user drops that segment. Got ExcludedByScan={excludedBySegment?.ExcludedByScan}");
        ctx.Assert.True(
            (await SearchByNameAsync(ctx, "books")).Count == 0,
            "…and Search must stop answering with a row the perimeter now excludes: Catalogo and " +
            "Ricerca disagreeing is the shape 11h left behind, and it is the half a user actually sees");

        ctx.Assert.True(
            !segmentNarrowed.NeedsScan,
            "adding a segment is a NARROWING, applied here in full, so the screen must not ask for a " +
            $"scan. Got needsScan={segmentNarrowed.NeedsScan}");
        ctx.Assert.True(
            segmentNarrowed.ExcludedCount >= 1,
            "…and 'no scan needed' is only honest because something was actually excluded; the " +
            $"reconciliation reported {segmentNarrowed.ExcludedCount}. Reporting a clean no-op while " +
            "excluding nothing was the defect, not the fix");

        var sibling = await FindFileRowAsync(ctx, ctx.SourceVolumeId, keptPath);
        ctx.Assert.True(
            sibling is { IsIncluded: true, IsPresent: true },
            "the exclusion must be scoped to the segment and not swallow the rest of the root — a " +
            "pass that excluded everything would satisfy every assertion above. Got " +
            $"IsIncluded={sibling?.IsIncluded}, IsPresent={sibling?.IsPresent}");

        var segmentDir = await FindDirectoryRowAsync(ctx, ctx.SourceVolumeId, segmentFolderPath);
        ctx.Assert.True(
            segmentDir is { IsPresent: true },
            "and the excluded FOLDER exists on disk, so it stays present: directories carry no " +
            "inclusion flag and a folder that is there is there (11g)");

        // ── and back out again, still without a single scan ───────────────────
        var segmentWidened = await SetExcludedPathsAsync(ctx);
        ctx.Log(
            $"excluded segment dropped: included={segmentWidened.IncludedCount} " +
            $"excluded={segmentWidened.ExcludedCount} needsScan={segmentWidened.NeedsScan}");

        var readmitted = await FindFileRowAsync(ctx, ctx.SourceVolumeId, segmentPath);
        ctx.Assert.True(
            readmitted is { IsIncluded: true, IsPresent: true, ExcludedByPath: false },
            "dropping the segment must re-admit those rows with NO scan (§4): every descendant " +
            "carries the segment in its own path, so the reconciler re-decides it from the catalog " +
            "without reading a byte of disk. Got " +
            $"IsIncluded={readmitted?.IsIncluded}, IsPresent={readmitted?.IsPresent}, " +
            $"ExcludedByPath={readmitted?.ExcludedByPath}");

        var findableAgain = await SearchByNameAsync(ctx, "books");
        ctx.Assert.True(
            readmitted is not null && findableAgain.Count == 1 && findableAgain[0] == readmitted.Id,
            "…and Search must agree with the Catalog again, still without a scan: the reconciliation " +
            $"has to put the pruned FTS entry back. Got {findableAgain.Count} hit(s).");

        ctx.Assert.True(
            segmentWidened.NeedsScan,
            "…while dropping a segment IS a widening for everything under it that was never indexed " +
            "in the first place, so the screen must still ask for a scan. Nothing can resurrect a row " +
            $"that does not exist. Got needsScan={segmentWidened.NeedsScan}");
    }

    /// <summary>
    /// Sets the global type allow-list through the real service, exactly as the Setup screen does.
    /// No arguments = every type allowed, which is the harness default.
    /// </summary>
    private static Task SetAllowedExtensionsAsync(ScenarioContext ctx, params string[] extensions) =>
        ctx.Env.WithScopeAsync<object?>(async sp =>
        {
            await sp.GetRequiredService<FilterSettingsService>().UpdateAsync(
                new FilterSettingsDto([.. extensions], []), ctx.Ct);
            return null;
        });

    /// <summary>
    /// Sets the global excluded path segments through the real <see cref="FilterSettingsService"/>,
    /// exactly as the Setup screen does, and hands back what it reported — the counts and
    /// <c>NeedsScan</c> are half of what A2 is about, so they are asserted rather than discarded.
    /// No arguments = nothing excluded, which is both the harness default and the state this
    /// scenario has to leave behind.
    ///
    /// <para>The allow-list travels in the same DTO because the settings are saved as a whole, and
    /// it is passed EMPTY (= every type) on purpose: by the time this runs the scenario has already
    /// segmentWidened the types back, so writing them again changes nothing, and any other value would
    /// quietly mix a type decision into a test about paths.</para>
    /// </summary>
    private static Task<ReconcileResultDto> SetExcludedPathsAsync(
        ScenarioContext ctx, params string[] segments) =>
        ctx.Env.WithScopeAsync(sp =>
            sp.GetRequiredService<FilterSettingsService>().UpdateAsync(
                new FilterSettingsDto([], [.. segments]), ctx.Ct));

    /// <summary>Toggles a watched root through the real service, exactly as the API does.</summary>
    private static Task UpdateRootAsync(ScenarioContext ctx, int rootId, bool isActive) =>
        ctx.Env.WithScopeAsync<object?>(async sp =>
        {
            await sp.GetRequiredService<WatchedRootsService>().UpdateAsync(
                rootId, new UpdateWatchedRootRequest(isActive, FilterOverride: null), ctx.Ct);
            return null;
        });
}
