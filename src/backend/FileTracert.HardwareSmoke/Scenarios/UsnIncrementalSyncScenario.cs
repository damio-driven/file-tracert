using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// Step 14d on real files: the short road works, and it is the SHORT one.
///
/// <para>A real full scan indexes a fixture area and leaves a journal cursor behind. Then work is
/// done <em>outside the application</em> with plain BCL calls — a file created, one renamed, one
/// deleted — and the only thing that runs afterwards is <see cref="UsnDeltaApplier"/>. No scan.
/// The catalog must converge anyway, which is the whole promise of CLAUDE.md §1.2 and the one
/// thing no in-process test can prove: this reads the volume's actual NTFS change journal.</para>
///
/// <para>Two assertions carry the weight. <c>LastFullScanUtc</c> must not have moved — otherwise
/// the convergence would just be a scan, and the scenario would be measuring the wrong road. And
/// the renamed file must keep its ROW IDENTITY: the FRN is the file's real name to NTFS, so a
/// rename done behind our back has to land on the row a queued operation already points at
/// (§5), not create a second one.</para>
///
/// <para>Skipped rather than failed when the volume did not get the journal engine: that means
/// the harness is running unelevated or on a filesystem without a journal, which is a fact about
/// the machine and not a defect. A vacuous PASS would be the worse answer — that is exactly the
/// trap step 13 found in the USN unit tests.</para>
/// </summary>
public sealed class UsnIncrementalSyncScenario : Scenario
{
    private const string KeptFile = @"usn\keep.dat";
    private const string RenamedFile = @"usn\rename-me.dat";
    private const string DeletedFile = @"usn\delete-me.dat";

    /// <summary>The name the renamed file ends up with, and the one only the journal can report.</summary>
    private const string NewName = "renamed-outside.dat";

    /// <summary>Created behind the application's back, after the scan that would have found it.</summary>
    private const string BornOutside = @"usn\born-outside.dat";

    private const long BornOutsideBytes = 12 * 1024;

    public override string Name => "usn-incremental-sync";

    public override string Description =>
        "Work done outside the app reaches the index through the journal alone, with no full scan.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange: three real files, one real scan ──────────────────────────
        ctx.Source.CreateFile(KeptFile, 8 * 1024);
        var renamedFullPath = ctx.Source.CreateFile(RenamedFile, 16 * 1024);
        var deletedFullPath = ctx.Source.CreateFile(DeletedFile, 4 * 1024);

        await EnsureWatchedRootAsync(ctx, ctx.Source, ctx.SourceVolumeId);
        var firstScan = await ScanVolumeAsync(ctx, ctx.SourceVolumeId);
        ctx.Log($"full scan: {firstScan.TotalSeconds:0.00}s");

        var (engine, scannedAt, cursor, journalId) = await ReadVolumeStateAsync(ctx);

        // The gate is the CURSOR, not the engine that walked. Since A4 a watched subfolder is
        // walked by enumeration and still checkpoints the journal — which is precisely the handover
        // this scenario should be exercising, so asking "was it the MFT engine?" would skip the
        // interesting case and call it a machine fact. Without a cursor there is genuinely no
        // journal to read (unelevated, or a filesystem that has none), and that is still a SKIP:
        // a vacuous PASS would be the worse answer, which is the trap step 13 found.
        if (cursor is null || journalId is null)
        {
            throw new ScenarioSkippedException(
                $"the volume has no journal cursor (walked by the {engine} engine), " +
                "so there is no delta to read — run the harness elevated on NTFS.");
        }

        ctx.Log($"walked by the {engine} engine; journal cursor after the scan: usn={cursor} id={journalId}");

        var keptPath = ctx.Source.RelativePath(KeptFile);
        var deletedPath = ctx.Source.RelativePath(DeletedFile);
        var oldNamePath = ctx.Source.RelativePath(RenamedFile);
        var newNamePath = ScanPath.Join(ScanPath.Parent(oldNamePath), NewName);
        var bornPath = ctx.Source.RelativePath(BornOutside);

        var beforeRename = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, oldNamePath, "arrange");
        if (beforeRename is null) return;

        // ── act: change the disk behind the application's back ────────────────
        // Plain BCL calls, no product code involved — which is the point: only the volume's own
        // change journal knows any of this happened.
        File.Move(renamedFullPath, Path.Combine(Path.GetDirectoryName(renamedFullPath)!, NewName));
        File.Delete(deletedFullPath);
        ctx.Source.CreateFile(BornOutside, BornOutsideBytes);

        var sync = await SyncVolumeAsync(ctx, ctx.SourceVolumeId);
        ctx.Log(
            $"delta: {sync.Status} ({sync.Reason}) — indexed={sync.Indexed} absent={sync.MarkedAbsent} " +
            $"excluded={sync.Excluded} dirs={sync.DirectoriesTouched} unplaced={sync.Unresolved}");

        ctx.Assert.True(
            sync.Status == UsnSyncStatus.Applied,
            $"the delta must have been applied, not '{sync.Status}' ({sync.Reason})");

        // ── assert: the catalog converged, and no scan did it ─────────────────
        var (_, scannedAfter, cursorAfter, _) = await ReadVolumeStateAsync(ctx);

        ctx.Assert.True(
            scannedAfter == scannedAt,
            $"no full scan may have run: LastFullScanUtc was {scannedAt:O} and is now {scannedAfter:O}");
        ctx.Assert.True(
            cursorAfter > cursor,
            $"the cursor must have advanced past {cursor}, but it is {cursorAfter}");

        var born = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, bornPath, "created outside");
        if (born is not null)
        {
            ctx.Assert.True(
                born.SizeBytes == BornOutsideBytes,
                $"the created file must carry the size read from disk: expected {BornOutsideBytes}, got {born.SizeBytes}");
            ctx.Assert.True(born.IsPresent && born.IsIncluded, "the created file must be present and included");
        }

        var renamed = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, newNamePath, "renamed outside");
        if (renamed is not null)
        {
            // The FRN is the file's identity to NTFS, so the rename must land on the row that was
            // already there — anything else silently orphans whatever points at it (§5).
            ctx.Assert.True(
                renamed.Id == beforeRename.Id,
                $"the rename must keep the row identity: was #{beforeRename.Id}, now #{renamed.Id}");
            ctx.Assert.True(renamed.IsPresent && renamed.IsIncluded, "the renamed file must stay indexed");
        }

        var underOldName = await FindFileRowAsync(ctx, ctx.SourceVolumeId, oldNamePath);
        ctx.Assert.True(
            underOldName is null,
            "the old name must not survive as a second row");

        var deleted = await FindFileRowAsync(ctx, ctx.SourceVolumeId, deletedPath);
        if (deleted is null)
        {
            ctx.Assert.Fail("the deleted file's row must survive, flagged — never deleted (§6)");
        }
        else
        {
            ctx.Assert.True(!deleted.IsPresent, "the deleted file must be flagged absent");
            ctx.Assert.True(
                deleted.IsIncluded,
                "…and still included: presence and inclusion are different facts (§6)");
        }

        var kept = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, keptPath, "untouched");
        if (kept is not null)
        {
            // The one thing a delta must never do: read "not mentioned" as "not there".
            ctx.Assert.True(
                kept.IsPresent && kept.IsIncluded,
                "a file the delta never mentioned must come out of it untouched");
        }

        // The search index follows the rows, so the file that only the journal knows about is
        // findable without a scan — Catalogo and Ricerca must not disagree.
        var hits = await SearchByNameAsync(ctx, "born-outside");
        ctx.Assert.True(
            born is not null && hits.Contains(born.Id),
            "the file the delta added must be findable in Search");

        AssertNoPartialsAnywhere(ctx);
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
