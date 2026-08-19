using FileTracert.Business.Filtering;
using FileTracert.Contracts.Scanning;
using FluentAssertions;

namespace FileTracert.Tests.Business;

/// <summary>
/// E7 — the scan resolves the governing watched root for every enumerated item. The old shape
/// rebuilt a <c>Where</c> + <c>OrderByDescending</c> + <c>FirstOrDefault</c> chain per item, and
/// <see cref="FileTracert.Contracts.Scanning.ScanPath.IsWithin"/> allocated a <c>root + '\'</c>
/// string per candidate on top of that — on a volume with three million entries that is millions
/// of enumerators, sort buffers and strings for an answer that never changes shape.
///
/// Two things are asserted, in that order of importance:
/// <list type="number">
/// <item><b>Same answer</b> — the ordered walk picks exactly the root the filter-and-sort picked,
/// including the ties and the segment boundary.</item>
/// <item><b>No allocation</b> — measured with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>, which is a counter, not a stopwatch. Zero
/// is the assertion because zero is stable: any reintroduction of LINQ or of the prefix
/// concatenation makes it non-zero immediately, whatever the machine.</item>
/// </list>
/// </summary>
public sealed class RootsBySpecificityTests
{
    private static readonly string[] Roots = ["Media", @"Media\Foto", "Documenti", @"Media\Foto\Raw"];

    /// <summary>The shape this replaced, kept here as the oracle the new one must agree with.</summary>
    private static string? FilterAndSort(IEnumerable<string> roots, string path) =>
        roots.Where(r => FileTracert.Contracts.Scanning.ScanPath.IsWithin(path, r))
             .OrderByDescending(r => r.Length)
             .FirstOrDefault();

    [Theory]
    [InlineData(@"Media\Foto\Raw\a.cr2")]      // deepest of three nested roots
    [InlineData(@"Media\Foto\a.jpg")]
    [InlineData(@"Media\a.jpg")]
    [InlineData(@"media\FOTO\a.jpg")]          // case-insensitive
    [InlineData(@"Mediateca\a.jpg")]           // segment-aware: NOT inside "Media"
    [InlineData(@"Media\Fotografie\a.jpg")]    // ditto one level down
    [InlineData(@"Documenti")]                 // the root itself, exact match
    [InlineData(@"Altro\a.jpg")]               // no root at all
    [InlineData(@"")]                          // the volume root as an item
    public void The_ordered_walk_answers_what_the_filter_and_sort_answered(string path)
    {
        var ordered = RootsBySpecificity.Of(Roots);

        ordered.Governing(path).Should().Be(FilterAndSort(Roots, path));
    }

    [Fact]
    public void The_volume_root_contains_everything_including_itself()
    {
        var ordered = RootsBySpecificity.Of([""]);

        ordered.Governing(@"Anything\at\all").Should().Be("");
        ordered.Governing("").Should().Be("");
    }

    [Fact]
    public void A_longer_root_wins_however_the_set_is_given()
    {
        // The ordering is the type's job, not the caller's — a set handed over shortest-first
        // must answer the same as the same set handed over deepest-first.
        RootsBySpecificity.Of(["Media", @"Media\Foto"]).Governing(@"Media\Foto\a.jpg")
            .Should().Be(@"Media\Foto");
        RootsBySpecificity.Of([@"Media\Foto", "Media"]).Governing(@"Media\Foto\a.jpg")
            .Should().Be(@"Media\Foto");
    }

    [Fact]
    public void Resolving_a_million_items_allocates_nothing()
    {
        var ordered = RootsBySpecificity.Of(Roots);
        var paths = new[]
        {
            @"Media\Foto\Raw\a.cr2", @"Media\Foto\b.jpg", @"Media\c.mp4",
            @"Documenti\d.pdf", @"Mediateca\e.jpg", @"Altro\f.txt",
        };

        // Warm-up: first-call JIT and the string literals are not what is being measured.
        foreach (var p in paths) ordered.Governing(p);

        const int iterations = 200_000;
        var before = GC.GetAllocatedBytesForCurrentThread();

        string? last = null;
        for (int i = 0; i < iterations; i++)
        {
            last = ordered.Governing(paths[i % paths.Length]);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Asserted so the loop cannot be optimised away, and so a zero-allocation walk that
        // stopped answering correctly would still be caught here.
        last.Should().Be(FilterAndSort(Roots, paths[(iterations - 1) % paths.Length]));
        allocated.Should().Be(0,
            "{0} resolutions must not allocate — the old chain allocated an enumerator, a sort " +
            "buffer and a prefix string per item", iterations);
    }
}
