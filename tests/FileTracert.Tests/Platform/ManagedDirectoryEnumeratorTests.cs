using FileTracert.Contracts.Platform;
using FileTracert.Platform;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Platform;

public sealed class ManagedDirectoryEnumeratorTests : IDisposable
{
    private readonly string _root;
    private readonly ManagedDirectoryEnumerator _sut = new(NullLogger<ManagedDirectoryEnumerator>.Instance);

    public ManagedDirectoryEnumeratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "filetracert-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello"); // 5 bytes
        File.WriteAllText(Path.Combine(_root, "sub", "b.txt"), "world!"); // 6 bytes
    }

    [Fact]
    public void Enumerate_returns_files_and_directories_recursively()
    {
        var entries = _sut.Enumerate(_root, string.Empty, CancellationToken.None).ToList();

        entries.Select(e => e.RelativePath)
            .Should()
            .BeEquivalentTo("a.txt", "sub", Path.Combine("sub", "b.txt"));
    }

    [Fact]
    public void Enumerate_populates_size_and_directory_flag()
    {
        var entries = _sut.Enumerate(_root, string.Empty, CancellationToken.None).ToList();

        var file = entries.Single(e => e.RelativePath == "a.txt");
        file.IsDirectory.Should().BeFalse();
        file.SizeBytes.Should().Be(5);

        var directory = entries.Single(e => e.RelativePath == "sub");
        directory.IsDirectory.Should().BeTrue();
        directory.SizeBytes.Should().Be(0);

        var nested = entries.Single(e => e.RelativePath == Path.Combine("sub", "b.txt"));
        nested.SizeBytes.Should().Be(6);
    }

    [Fact]
    public void Enumerate_missing_root_yields_empty_without_throwing()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var act = () => _sut.Enumerate(missing, string.Empty, CancellationToken.None).ToList();

        act.Should().NotThrow();
        act().Should().BeEmpty();
    }

    [Fact]
    public void Enumerate_honors_relative_root()
    {
        var entries = _sut.Enumerate(_root, "sub", CancellationToken.None).ToList();

        entries.Select(e => e.RelativePath).Should().ContainSingle()
            .Which.Should().Be(Path.Combine("sub", "b.txt"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
