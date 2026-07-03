using System.Diagnostics;
using FileTracert.Contracts.Platform;

namespace FileTracert.HardwareSmoke;

/// <summary>
/// Drives the real <see cref="IFileMover"/> against real files to smoke-test the hardware path
/// (cross-volume copy, verify, recycle-bin delete, intra-volume move, folder subtree move).
///
/// It NEVER operates on the Source originals: it duplicates them into a fresh work directory under
/// ScratchPath and moves the duplicates. Deletes go to the Recycle Bin (reversible). Every step is
/// reported with its outcome and timing; failures are reported, not swallowed silently.
/// </summary>
public sealed class HardwareSmokeRunner
{
    private readonly IFileMover _mover;
    private readonly IVolumePathResolver _resolver;
    private readonly Action<string> _report;

    public HardwareSmokeRunner(IFileMover mover, IVolumePathResolver resolver, Action<string> report)
    {
        _mover = mover;
        _resolver = resolver;
        _report = report;
    }

    /// <summary>
    /// Validates the guard-rails and, only if they pass, runs the smoke scenarios. Returns true
    /// when the harness actually ran (guard passed), false when it declined (disabled/unsafe).
    /// </summary>
    public bool Run(HardwareSmokeOptions options, IReadOnlyList<string> productionWatchedRootPaths)
    {
        var guard = HardwareSmokeGuard.Validate(options, productionWatchedRootPaths);
        if (!guard.Ok)
        {
            _report($"SKIPPED: {guard.Reason}");
            return false;
        }

        _report("Hardware smoke harness starting.");
        _report($"  Source : {Path.GetFullPath(options.SourcePath)}");
        _report($"  Target : {Path.GetFullPath(options.TargetPath)}");
        _report($"  Scratch: {Path.GetFullPath(options.ScratchPath)}");

        var workDir = DuplicateSourceIntoScratch(options);
        _report($"Duplicated source into work area: {workDir}");

        RunStep("intra-volume move", () => IntraVolumeMove(workDir));
        RunStep("cross-volume move (copy → verify → finalize → recycle)", () => CrossVolumeMove(workDir, options));
        RunStep("folder subtree move", () => FolderSubtreeMove(workDir, options));

        _report("Hardware smoke harness finished.");
        return true;
    }

    /// <summary>
    /// Copies the whole Source tree into a fresh <c>_smoke-work-{guid}</c> directory under Scratch
    /// and returns it. Pure file IO — the Source originals are never modified. Public so the
    /// harness tests can assert it operates on copies.
    /// </summary>
    public static string DuplicateSourceIntoScratch(HardwareSmokeOptions options)
    {
        var source = Path.GetFullPath(options.SourcePath);
        var workDir = Path.Combine(Path.GetFullPath(options.ScratchPath), $"_smoke-work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(workDir, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(workDir, Path.GetRelativePath(source, file)), overwrite: true);

        return workDir;
    }

    // ── scenarios ───────────────────────────────────────────────────────────

    private void IntraVolumeMove(string workDir)
    {
        var file = Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories).FirstOrDefault();
        if (file is null) { _report("  (no files to move)"); return; }

        var movedDir = Path.Combine(workDir, "_moved-intra");
        Directory.CreateDirectory(movedDir);
        var dest = Path.Combine(movedDir, Path.GetFileName(file));

        var (guid, srcRel) = _resolver.Resolve(file);
        var (_, dstRel) = _resolver.Resolve(dest);
        _mover.MoveIntraVolume(guid, srcRel, dstRel);

        _report($"  moved '{Path.GetFileName(file)}' within the work volume.");
    }

    private void CrossVolumeMove(string workDir, HardwareSmokeOptions options)
    {
        var file = Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories).FirstOrDefault();
        if (file is null) { _report("  (no files to move)"); return; }

        var targetDir = Path.Combine(Path.GetFullPath(options.TargetPath), $"_smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDir);
        var finalAbs = Path.Combine(targetDir, Path.GetFileName(file));
        var partialAbs = finalAbs + ".fadit-partial";

        var (srcGuid, srcRel) = _resolver.Resolve(file);
        var (tgtGuid, partialRel) = _resolver.Resolve(partialAbs);
        var (_, finalRel) = _resolver.Resolve(finalAbs);

        _mover.CopyFileAsync(srcGuid, srcRel, tgtGuid, partialRel, null, CancellationToken.None)
              .GetAwaiter().GetResult();

        if (!_mover.Verify(srcGuid, srcRel, tgtGuid, partialRel, withHash: false))
            throw new InvalidOperationException("verify failed after cross-volume copy.");

        _mover.FinalizePartial(tgtGuid, partialRel, finalRel);
        _mover.DeleteToRecycleBin(srcGuid, srcRel); // reversible

        _report($"  copied '{Path.GetFileName(file)}' cross-volume, verified, finalized; source sent to Recycle Bin.");
    }

    private void FolderSubtreeMove(string workDir, HardwareSmokeOptions options)
    {
        var subtree = Directory.EnumerateDirectories(workDir)
            .FirstOrDefault(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any());
        if (subtree is null) { _report("  (no populated subtree to move)"); return; }

        var targetDir = Path.Combine(Path.GetFullPath(options.TargetPath), $"_smoke-tree-{Guid.NewGuid():N}");
        var (srcGuid, srcRel) = _resolver.Resolve(subtree);
        var (tgtGuid, dstRel) = _resolver.Resolve(Path.Combine(targetDir, Path.GetFileName(subtree)));

        // Copy each file, then recycle the source subtree (deepest-first is the mover's own job).
        foreach (var file in Directory.EnumerateFiles(subtree, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(subtree, file);
            var (_, fileSrcRel) = _resolver.Resolve(file);
            var (_, fileDstRel) = _resolver.Resolve(Path.Combine(targetDir, Path.GetFileName(subtree), rel));
            var partial = fileDstRel + ".fadit-partial";
            _mover.CopyFileAsync(srcGuid, fileSrcRel, tgtGuid, partial, null, CancellationToken.None)
                  .GetAwaiter().GetResult();
            _mover.FinalizePartial(tgtGuid, partial, fileDstRel);
        }
        _mover.DeleteToRecycleBin(srcGuid, srcRel);

        _report($"  moved subtree '{Path.GetFileName(subtree)}' cross-volume; source subtree sent to Recycle Bin.");
    }

    // ── step timing / non-silent error reporting ──────────────────────────────

    private void RunStep(string name, Action step)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            step();
            sw.Stop();
            _report($"OK   [{name}] in {sw.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _report($"FAIL [{name}] after {sw.ElapsedMilliseconds} ms: {ex.Message}");
        }
    }
}
