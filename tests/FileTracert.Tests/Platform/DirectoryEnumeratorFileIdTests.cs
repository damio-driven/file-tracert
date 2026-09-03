using FileTracert.Contracts.Platform;
using FileTracert.Platform;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Platform;

/// <summary>
/// The enumeration engine has to hand back the same identity the journal speaks in — the NTFS
/// file reference number — or the incremental path cannot place a single record: it rebuilds a
/// path by walking up from the PARENT's FRN through the catalog's directory rows, and a row
/// without one answers nothing (see the A4 note in CLAUDE.md).
///
/// <para>There is no oracle to compare against here that would not be a second copy of the
/// production call, so these cases assert the PROPERTIES the product relies on instead: an
/// identity that exists, that is unique per object, and — the one that matters — that survives a
/// rename, because that is exactly what a path cannot do.</para>
/// </summary>
public sealed class DirectoryEnumeratorFileIdTests : IDisposable
{
    private readonly string _root;
    private readonly ManagedDirectoryEnumerator _sut = new(NullLogger<ManagedDirectoryEnumerator>.Instance);

    public DirectoryEnumeratorFileIdTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "filetracert-frn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(_root, "sub", "b.txt"), "world!");
    }

    [Fact]
    public void Every_entry_carries_a_file_reference_number()
    {
        var entries = _sut.Enumerate(_root, string.Empty, CancellationToken.None).ToList();

        entries.Should().HaveCount(3);
        entries.Should().OnlyContain(e => e.Frn != null && e.Frn != 0);
    }

    [Fact]
    public void Distinct_objects_get_distinct_references()
    {
        var entries = _sut.Enumerate(_root, string.Empty, CancellationToken.None).ToList();

        entries.Select(e => e.Frn).Distinct().Should().HaveCount(entries.Count);
    }

    [Fact]
    public void A_rename_keeps_the_reference_a_path_would_have_lost()
    {
        var before = _sut.Enumerate(_root, string.Empty, CancellationToken.None)
            .Single(e => e.RelativePath == "a.txt");

        File.Move(Path.Combine(_root, "a.txt"), Path.Combine(_root, "renamed.txt"));

        var after = _sut.Enumerate(_root, string.Empty, CancellationToken.None)
            .Single(e => e.RelativePath == "renamed.txt");

        after.Frn.Should().Be(before.Frn);
    }

    [Fact]
    public void The_reference_is_the_one_the_watched_root_itself_has()
    {
        // The walk starts INSIDE the root and never yields it, yet the delta resolves records
        // whose parent IS the root. Its identity therefore has to be askable on its own.
        var subEntry = _sut.Enumerate(_root, string.Empty, CancellationToken.None)
            .Single(e => e.RelativePath == "sub");

        var asked = _sut.TryGetFileId(Path.Combine(_root, "sub"));

        asked.Should().Be(subEntry.Frn);
    }

    [Fact]
    public void Asking_for_a_missing_path_answers_nothing_instead_of_throwing()
    {
        var act = () => _sut.TryGetFileId(Path.Combine(_root, "does-not-exist"));

        act.Should().NotThrow();
        act().Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
