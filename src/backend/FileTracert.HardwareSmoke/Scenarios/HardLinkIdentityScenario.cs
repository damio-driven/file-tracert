using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// Two paths, one file, on a real NTFS volume — the shape that made the hybrid engine fail a full
/// scan of the developer's own system drive with
/// <c>UNIQUE constraint failed: Files.VolumeId, Files.UsnFileRef</c>.
///
/// <para>A <c>Files</c> row is a PATH, and NTFS lets many paths name one file. Hard links are not
/// exotic: Git for Windows ships every <c>libexec\git-core</c> tool as a link to its twin in
/// <c>bin</c>, and a probe of one real perimeter found 153 file references claimed by more than
/// one path, up to seven paths for a single file. But <c>(VolumeId, UsnFileRef)</c> is UNIQUE
/// (§6), so at most one row may carry the identity — and until A4 the merge got that for free,
/// because only the MFT snapshot produced file references and it keeps one path per FRN (P1).
/// The enumeration walk reports every path.</para>
///
/// <para>What this asserts is the fix's contract, not merely the absence of the crash: BOTH paths
/// stay indexed (dropping one would be P1 applied to the user's data), exactly ONE of them carries
/// the identity, and a re-scan neither loses a row nor moves the claim. It has to run on the iron
/// because the thing under test is whether the volume really hands the same file reference number
/// for two different paths — a fake enumerator would just be repeating the assumption.</para>
/// </summary>
public sealed class HardLinkIdentityScenario : Scenario
{
    private const string Original = @"links\tool.dll";
    private const string Link = @"links\bin\tool.dll";
    private const long Bytes = 6 * 1024;

    public override string Name => "hard-link-identity";

    public override string Description =>
        "Two hard-linked paths: both indexed, one carries the file reference, and a re-scan keeps both.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange: one real file reachable through two real paths ───────────
        var originalFullPath = ctx.Source.CreateFile(Original, Bytes);
        var linkFullPath = ctx.Source.FullPath(Link);
        Directory.CreateDirectory(Path.GetDirectoryName(linkFullPath)!);

        if (!CreateHardLinkW(linkFullPath, originalFullPath, nint.Zero))
        {
            // ERROR_NOT_SAME_DEVICE / ERROR_INVALID_FUNCTION on a filesystem without hard links is
            // a fact about the machine, not a defect — and a vacuous PASS would be the worse
            // answer (the trap step 13 found in the USN unit tests).
            var win32 = new Win32Exception(Marshal.GetLastWin32Error());
            throw new ScenarioSkippedException(
                $"this volume will not create a hard link ({win32.Message.Trim()}) — needs NTFS.");
        }

        await EnsureWatchedRootAsync(ctx, ctx.Source, ctx.SourceVolumeId);

        // ── act: a real full scan, which is what used to throw ────────────────
        var elapsed = await ScanVolumeAsync(ctx, ctx.SourceVolumeId);
        ctx.Log($"full scan over a hard-linked pair: {elapsed.TotalSeconds:0.00}s");

        // ── assert: both paths indexed, exactly one claims the identity ───────
        var originalPath = ctx.Source.RelativePath(Original);
        var linkPath = ctx.Source.RelativePath(Link);

        var first = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, originalPath, "the original path");
        var second = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, linkPath, "the hard-linked path");
        if (first is null || second is null) return;

        ctx.Assert.True(
            first.Id != second.Id,
            "two paths on disk must be two rows in the catalog: a row is a path");
        ctx.Assert.True(
            first.IsPresent && first.IsIncluded && second.IsPresent && second.IsIncluded,
            "both paths exist on disk and pass the filter, so both must be present and included");
        ctx.Assert.True(
            first.SizeBytes == Bytes && second.SizeBytes == Bytes,
            $"both rows describe the same {Bytes} bytes: got {first.SizeBytes} and {second.SizeBytes}");

        var claims = new[] { first, second }.Count(f => f.UsnFileRef is not null);
        ctx.Assert.True(
            claims == 1,
            "exactly one of the two rows may carry the file reference — the index is unique and " +
            $"a hard link shares the number; {claims} of 2 carry one");

        ctx.Log($"identity held by row #{(first.UsnFileRef is not null ? first.Id : second.Id)}");

        // ── and a re-scan must converge, not flap ─────────────────────────────
        await ScanVolumeAsync(ctx, ctx.SourceVolumeId);

        var firstAgain = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, originalPath, "after re-scan");
        var secondAgain = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, linkPath, "after re-scan");
        if (firstAgain is null || secondAgain is null) return;

        ctx.Assert.True(
            firstAgain.Id == first.Id && secondAgain.Id == second.Id,
            "a re-scan must land on the rows that were already there, not build new ones");
        ctx.Assert.True(
            firstAgain.IsPresent && secondAgain.IsPresent,
            "neither path may come out of a re-scan marked absent: the walk saw both");
        ctx.Assert.True(
            (firstAgain.UsnFileRef is not null) == (first.UsnFileRef is not null),
            "the claim must not move between the two paths from one scan to the next");

        AssertNoPartialsAnywhere(ctx);
    }

    /// <summary>
    /// The product never CREATES a hard link — only a test needs to — so this interop lives here
    /// with the scenario that needs it rather than widening the platform surface (§3).
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string linkFileName, string existingFileName, nint securityAttributes);
}
