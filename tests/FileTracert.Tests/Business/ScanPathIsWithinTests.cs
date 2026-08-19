using FileTracert.Contracts.Scanning;
using FluentAssertions;

namespace FileTracert.Tests.Business;

/// <summary>
/// <see cref="ScanPath.IsWithin"/> is not a scan detail: it is the containment half of
/// <see cref="ScanPath.Overlaps"/>, which is THE predicate the enqueue guard asks "is another job
/// already working here?" (§5, step 9c), the rule that drops an excluded folder's whole subtree,
/// and the one the snapshot replay uses to decide whether a queued path moved. Step 11e rewrote it
/// over spans to stop allocating a <c>root + '\'</c> prefix per call (E7), so it gets its own
/// equivalence test rather than being covered only through its callers.
///
/// The old spelling is kept here as the ORACLE. The claim is not "the new one looks right", it is
/// "the new one answers what the old one answered", on every case worth doubting: the volume root,
/// equality, the segment boundary that keeps <c>Docs</c> out of <c>Documents</c>, a path shorter
/// than the root, case folding, and non-ASCII — where <c>OrdinalIgnoreCase</c> folds per UTF-16
/// code unit, which is what makes the length-preserving assumption behind the span version hold.
/// </summary>
public sealed class ScanPathIsWithinTests
{
    /// <summary>The spelling the span version replaced.</summary>
    private static bool Oracle(string path, string root) =>
        root.Length == 0 ||
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase);

    public static TheoryData<string, string> Cases() => new()
    {
        // volume root contains everything, including itself and the empty path
        { "", "" },
        { @"Media\a.jpg", "" },
        { "Media", "" },

        // equality
        { "Media", "Media" },
        { @"Media\Foto", @"Media\Foto" },

        // strictly inside
        { @"Media\a.jpg", "Media" },
        { @"Media\Foto\Raw\a.cr2", "Media" },
        { @"Media\Foto\Raw\a.cr2", @"Media\Foto" },

        // segment boundary — the case that matters most, because getting it wrong makes two
        // unrelated operations look like they collide (or stops them from colliding)
        { "Mediateca", "Media" },
        { @"Mediateca\a.jpg", "Media" },
        { @"Media\Fotografie\a.jpg", @"Media\Foto" },
        { "MediaX", "Media" },

        // path shorter than the root
        { "Med", "Media" },
        { "", "Media" },
        { @"Media\Foto", @"Media\Foto\Raw" },

        // case folding, both directions
        { @"media\foto\a.jpg", @"Media\Foto" },
        { @"MEDIA\FOTO\a.jpg", @"media\foto" },
        { "MEDIA", "media" },

        // separator in the wrong place
        { @"Media\", "Media" },
        { @"\Media\a.jpg", "Media" },

        // non-ASCII: OrdinalIgnoreCase folds per code unit, so these must agree with the oracle
        // whatever it decides — the point is that the two spellings decide the SAME thing
        { @"Città\Foto\a.jpg", @"Città" },
        { @"CITTÀ\Foto\a.jpg", @"città" },
        { @"Straße\a.jpg", @"STRASSE" },
        { @"Ünico\a.jpg", @"ünico" },

        // surrogate pairs (outside the BMP): ordinal comparison works on code units, and both
        // spellings inherit that identically
        { "\U0001F4C1Foto\\a.jpg", "\U0001F4C1Foto" },
        { "\U0001F4C1Foto\\a.jpg", "\U0001F4C1FOTO" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_span_version_answers_what_the_concatenating_one_answered(string path, string root)
        => ScanPath.IsWithin(path, root).Should().Be(Oracle(path, root),
            "IsWithin({0}, {1})", path, root);

    /// <summary>
    /// The three answers the rule is actually FOR, stated outright, so a future change cannot
    /// satisfy the oracle by breaking both spellings the same way.
    /// </summary>
    [Fact]
    public void The_rule_itself()
    {
        ScanPath.IsWithin(@"Media\Foto\a.jpg", "Media").Should().BeTrue("a descendant is within");
        ScanPath.IsWithin("Media", "Media").Should().BeTrue("a root contains itself");
        ScanPath.IsWithin("Mediateca", "Media").Should().BeFalse(
            "containment ends on a segment boundary — Docs is not Documents");
    }

    /// <summary>Asking the question allocates nothing (E7): the answer is read, not built.</summary>
    [Fact]
    public void Asking_allocates_nothing()
    {
        ScanPath.IsWithin(@"Media\Foto\a.jpg", "Media");   // warm-up

        var before = GC.GetAllocatedBytesForCurrentThread();
        bool last = false;
        for (int i = 0; i < 100_000; i++)
            last = ScanPath.IsWithin(@"Media\Foto\a.jpg", "Media");

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        last.Should().BeTrue();
    }
}
