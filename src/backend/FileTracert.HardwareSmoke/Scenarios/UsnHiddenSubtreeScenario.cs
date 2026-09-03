using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// Step 16 / A3 on the REAL journal: a folder that turns Hidden takes the rows already catalogued
/// under it out of the perimeter, and the delta alone does it.
///
/// <para>A full scan never had this hole — it asks the perimeter about every directory of the
/// catalog when it closes, so each descendant of a newly hidden folder produces its own skipped
/// area. A delta cannot: it sees what CHANGED, and the files under that folder did not change, so
/// no journal record names them. Until this step they kept <c>IsIncluded = 1</c> until the next
/// full scan — navigable in the Catalog and findable in Search, inside a folder the perimeter
/// excludes.</para>
///
/// <para><b>The act is one attribute write on a directory, and nothing else.</b> No file is
/// touched, which is the point: if any of the rows below were reached because the harness had
/// rewritten them, the scenario would be measuring the merge instead of the subtree pass. What
/// reaches the product is a single <c>USN_REASON_BASIC_INFO_CHANGE</c> record for the folder, and
/// everything asserted afterwards has to follow from that record alone.</para>
///
/// <para><b>Two assertions carry the weight, and they are the two the task named.</b>
/// <c>LastFullScanUtc</c> must not have moved — otherwise the convergence would just be a scan and
/// the scenario would be measuring the wrong road, passing for a reason that has nothing to do with
/// the fix. And the files inside must come out <c>IsIncluded = 0</c> with <c>IsPresent = 1</c>: they
/// are still on disk, the perimeter simply no longer covers them, and collapsing those two facts
/// into one is exactly what 11g exists to prevent.</para>
///
/// <para>The cause is asserted too, because writing the wrong one is a silent, durable defect
/// rather than a visible one: this folder is HIDDEN, not under an excluded segment, so the row must
/// carry <c>ExcludedByScan</c> and not <c>ExcludedByPath</c>. Getting that backwards would let a
/// later Setup save walk the content of a hidden folder back into the Catalog — the regression 11h
/// exists to prevent, reached through step 16's new door.</para>
///
/// <para><b>Step 18 adds a second tick</b>: a file written inside the hidden folder and a folder
/// created there. Both records carry clean attributes of their own; what excludes them is the
/// parent's row, which the first tick stamped (<c>Directories.ExcludedByScan</c>). Before step 18
/// the first came back <c>IsIncluded = 1</c> and the second was catalogued.</para>
///
/// <para>Skipped rather than failed when the volume did not get the journal engine, for the reason
/// <see cref="UsnIncrementalSyncScenario"/> gives: unelevated, the scan falls back to enumeration,
/// and a scenario that then reported PASS would be asserting about a road it never took.</para>
/// </summary>
public sealed class UsnHiddenSubtreeScenario : Scenario
{
    private const string HiddenFolder = @"usnhidden\cache";
    private const string InsideOne = @"usnhidden\cache\cachedone.dat";
    private const string InsideTwo = @"usnhidden\cache\cachedtwo.dat";

    /// <summary>Step 18, second tick: born INSIDE the hidden folder, after it went hidden.</summary>
    private const string LateFolder = @"usnhidden\cache\newsub";
    private const string LateFile = @"usnhidden\cache\newsub\fresh.dat";

    /// <summary>A sibling OUTSIDE the folder: the exclusion has to be a subtree, not a sweep.</summary>
    private const string Outside = @"usnhidden\openroom.dat";

    public override string Name => "usn-hidden-subtree";

    public override string Description =>
        "A folder turning Hidden excludes the rows already catalogued under it, through the delta alone.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange: three real files, one real scan ──────────────────────────
        ctx.Source.CreateFile(InsideOne, 8 * 1024);
        ctx.Source.CreateFile(InsideTwo, 8 * 1024);
        ctx.Source.CreateFile(Outside, 8 * 1024);

        await EnsureWatchedRootAsync(ctx, ctx.Source, ctx.SourceVolumeId);
        var firstScan = await ScanVolumeAsync(ctx, ctx.SourceVolumeId);
        ctx.Log($"full scan: {firstScan.TotalSeconds:0.00}s");

        var (engine, scannedAt, cursor, journalId) = await ReadVolumeStateAsync(ctx);
        if (engine != VolumeScanEngine.UsnJournal || cursor is null || journalId is null)
        {
            throw new ScenarioSkippedException(
                $"the volume was indexed by the {engine} engine (cursor={cursor?.ToString() ?? "none"}), " +
                "so there is no journal to read a delta from — run the harness elevated on NTFS.");
        }

        ctx.Log($"journal cursor after the scan: usn={cursor} id={journalId}");

        var insideOnePath = ctx.Source.RelativePath(InsideOne);
        var insideTwoPath = ctx.Source.RelativePath(InsideTwo);
        var outsidePath = ctx.Source.RelativePath(Outside);
        var folderPath = ctx.Source.RelativePath(HiddenFolder);

        var arrangedOne = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, insideOnePath, "arrange");
        var arrangedTwo = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, insideTwoPath, "arrange");
        if (arrangedOne is null || arrangedTwo is null) return;

        ctx.Assert.True(
            arrangedOne is { IsIncluded: true, IsPresent: true } &&
            arrangedTwo is { IsIncluded: true, IsPresent: true },
            "arrange: both files under the folder start inside the perimeter");
        ctx.Assert.True(
            (await SearchByNameAsync(ctx, "cached")).Count == 2,
            "arrange: …and both are findable in Search, which is what makes their disappearance below mean something");

        // ── act: one attribute write on the FOLDER, nothing else ──────────────
        // Plain BCL, no product code: the only thing that knows this happened is the volume's own
        // change journal, and the files inside are not touched at all — NTFS does not propagate
        // Hidden to children, which is precisely why the exclusion has to be inherited (C16).
        var folder = new DirectoryInfo(ctx.Source.FullPath(HiddenFolder));
        folder.Attributes |= FileAttributes.Hidden;

        var sync = await SyncVolumeAsync(ctx, ctx.SourceVolumeId);
        ctx.Log(
            $"delta: {sync.Status} ({sync.Reason}) — indexed={sync.Indexed} absent={sync.MarkedAbsent} " +
            $"excluded={sync.Excluded} dirs={sync.DirectoriesTouched} unplaced={sync.Unresolved}");

        ctx.Assert.True(
            sync.Status == UsnSyncStatus.Applied,
            $"the delta must have been applied, not '{sync.Status}' ({sync.Reason})");

        // ── assert: no scan did this ──────────────────────────────────────────
        var (_, scannedAfter, cursorAfter, _) = await ReadVolumeStateAsync(ctx);

        ctx.Assert.True(
            scannedAfter == scannedAt,
            "no full scan may have run — a scan would produce the same rows by the road this " +
            $"scenario is NOT about: LastFullScanUtc was {scannedAt:O} and is now {scannedAfter:O}");
        ctx.Assert.True(
            cursorAfter > cursor,
            $"the cursor must have advanced past {cursor}, but it is {cursorAfter}");

        // ── assert: excluded, and not absent ──────────────────────────────────
        var afterOne = await FindFileRowAsync(ctx, ctx.SourceVolumeId, insideOnePath);
        var afterTwo = await FindFileRowAsync(ctx, ctx.SourceVolumeId, insideTwoPath);

        foreach (var (row, which) in new[] { (afterOne, InsideOne), (afterTwo, InsideTwo) })
        {
            if (row is null)
            {
                ctx.Assert.Fail($"'{which}': the row must survive, flagged — never deleted (§6)");
                continue;
            }

            ctx.Assert.True(
                !row.IsIncluded,
                $"'{which}': a file the delta never named must still leave the perimeter with the " +
                "folder above it — that inheritance is the whole of A3. Got IsIncluded=true");
            ctx.Assert.True(
                row.IsPresent,
                $"'{which}': …and it must NOT be flagged absent. It is on disk and nobody looked for " +
                "it; an exclusion is not an absence (§6). On disk right now: " +
                $"{File.Exists(ctx.Source.FullPath(which))}");
            ctx.Assert.True(
                row.ExcludedByScan,
                $"'{which}': the cause must be ExcludedByScan — the folder is HIDDEN, a fact of the " +
                "disk that no setting can retract. Got ExcludedByScan=false");
            ctx.Assert.True(
                !row.ExcludedByPath,
                $"'{which}': …and NOT ExcludedByPath, which reconciliation undoes on its own. " +
                "Recording that here would let a later Setup save walk a hidden folder's content " +
                "back into the Catalog — the 11h regression through step 16's new door");
            ctx.Assert.True(
                !row.ExcludedByRoot && !row.ExcludedByType,
                $"'{which}': no other cause applies — the root is active and the allow-list is open");
        }

        // ── assert: a subtree, not a sweep ────────────────────────────────────
        var untouched = await FindFileRowAsync(ctx, ctx.SourceVolumeId, outsidePath);
        ctx.Assert.True(
            untouched is { IsIncluded: true, IsPresent: true },
            "the sibling OUTSIDE the hidden folder must come out untouched: a pass that excluded the " +
            "whole volume would satisfy every assertion above. Got " +
            $"IsIncluded={untouched?.IsIncluded}, IsPresent={untouched?.IsPresent}");

        var folderRow = await FindDirectoryRowAsync(ctx, ctx.SourceVolumeId, folderPath);
        ctx.Assert.True(
            folderRow is { IsPresent: true },
            "the hidden FOLDER exists on disk, so it stays present — a folder that exists exists (11g)");
        ctx.Assert.True(
            folderRow is { ExcludedByScan: true },
            "step 18: …and its row must REMEMBER why its content is out, so the next tick can inherit " +
            $"it. Got ExcludedByScan={folderRow?.ExcludedByScan}");

        // The index has to follow the rows, and by directory: the Catalog hiding a row that Search
        // still answers with is the disagreement the user actually sees.
        ctx.Assert.True(
            (await SearchByNameAsync(ctx, "cached")).Count == 0,
            "Search must stop answering with the rows the perimeter now excludes");
        ctx.Assert.True(
            untouched is not null && (await SearchByNameAsync(ctx, "openroom")).Contains(untouched.Id),
            "…while the sibling outside it must still be findable: the prune is per directory, not per volume");

        // ── assert: replaying the same tick changes nothing ───────────────────
        // The cursor is written LAST (14d), so a crash costs a repeated delta and never a skipped
        // one — which is only safe if a repeat is a no-op. Cheap to ask here, and it is the one
        // property the whole crash-safety argument of this road rests on.
        var replay = await SyncVolumeAsync(ctx, ctx.SourceVolumeId);
        ctx.Log($"replay: {replay.Status} ({replay.Reason}) — excluded={replay.Excluded}");

        var replayedOne = await FindFileRowAsync(ctx, ctx.SourceVolumeId, insideOnePath);
        ctx.Assert.True(
            replayedOne is { IsIncluded: false, IsPresent: true },
            "a second pass over the same world must leave the row exactly as it was; got " +
            $"IsIncluded={replayedOne?.IsIncluded}, IsPresent={replayedOne?.IsPresent}");
        ctx.Assert.True(
            replayedOne is not null && afterOne is not null && replayedOne.Id == afterOne.Id,
            "…on the same row, not a second one");

        // ── step 18: the NEXT tick, with ordinary traffic inside the hidden folder ────────────
        // The two residuals of step 16, on the real journal. A file inside the hidden folder is
        // written (its own attributes are clean, its own path has no excluded segment), and a new
        // subfolder with a file is created there. Before step 18 the delta re-admitted the first
        // and catalogued the second: it judged each record on its own, and nothing it could read
        // said the folder above was hidden. Now the folder's row says so.
        File.WriteAllBytes(ctx.Source.FullPath(InsideOne), new byte[9 * 1024]);
        ctx.Source.CreateFile(LateFile, 4 * 1024);

        var second = await SyncVolumeAsync(ctx, ctx.SourceVolumeId);
        ctx.Log(
            $"second tick: {second.Status} ({second.Reason}) — indexed={second.Indexed} " +
            $"excluded={second.Excluded} dirs={second.DirectoriesTouched} unplaced={second.Unresolved}");
        ctx.Assert.True(
            second.Status == UsnSyncStatus.Applied,
            $"the second delta must have been applied, not '{second.Status}' ({second.Reason})");

        var (_, scannedAfterSecond, _, _) = await ReadVolumeStateAsync(ctx);
        ctx.Assert.True(
            scannedAfterSecond == scannedAt,
            "still no full scan: the second tick is the delta's own answer");

        var writtenInside = await FindFileRowAsync(ctx, ctx.SourceVolumeId, insideOnePath);
        ctx.Assert.True(
            writtenInside is { IsIncluded: false, IsPresent: true, ExcludedByScan: true },
            "a file WRITTEN inside the hidden folder must stay excluded — its record carries clean " +
            "attributes, the exclusion comes off the parent row (step 18). Got " +
            $"IsIncluded={writtenInside?.IsIncluded}, IsPresent={writtenInside?.IsPresent}, " +
            $"ExcludedByScan={writtenInside?.ExcludedByScan}");
        ctx.Assert.True(
            writtenInside is not null && afterOne is not null && writtenInside.Id == afterOne.Id,
            "…on the same row: the merge matches by FRN");

        var lateFolderRow = await FindDirectoryRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(LateFolder));
        ctx.Assert.True(
            lateFolderRow is null,
            "a folder CREATED inside the hidden one must never enter the catalog — as after a full " +
            "scan, which drops the whole subtree (step 18). Got a row with " +
            $"IsPresent={lateFolderRow?.IsPresent}");
        ctx.Assert.True(
            await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(LateFile)) is null,
            "…and neither must the file created under it");

        ctx.Assert.True(
            (await SearchByNameAsync(ctx, "cached")).Count == 0 &&
            (await SearchByNameAsync(ctx, "fresh")).Count == 0,
            "Search must answer with nothing from inside the hidden folder after the second tick either");

        var stillOutside = await FindFileRowAsync(ctx, ctx.SourceVolumeId, outsidePath);
        ctx.Assert.True(
            stillOutside is { IsIncluded: true, IsPresent: true },
            "the sibling outside the hidden folder is still untouched after the second tick");
    }

    private static Task<UsnSyncResult> SyncVolumeAsync(ScenarioContext ctx, int volumeId) =>
        ctx.Env.WithScopeAsync(sp =>
            sp.GetRequiredService<UsnDeltaApplier>().SyncVolumeAsync(volumeId, ctx.Ct));

    private static Task<(VolumeScanEngine Engine, DateTime? ScannedAt, long? Cursor, long? JournalId)>
        ReadVolumeStateAsync(ScenarioContext ctx) =>
        ctx.Env.WithDbAsync(async db =>
        {
            var volume = await db.Volumes.AsNoTracking()
                .FirstAsync(v => v.Id == ctx.SourceVolumeId, ctx.Ct);
            return (volume.ScanEngine, volume.LastFullScanUtc, volume.LastUsn, volume.UsnJournalId);
        });
}
