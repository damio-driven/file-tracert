using FileTracert.Business.Filtering;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Business;

/// <summary>
/// The single-path half of "which filter governs this?" — the rule the rename asks (C19) and the
/// scan uses for its own root matching. Two spellings of "most specific" is how a file ends up
/// included by the scan and excluded by the rename, so the rule is tested on its own.
/// </summary>
public sealed class RootFilterResolverTests : IDisposable
{
    private const int VolumeId = 1;
    private readonly SqliteInMemoryContext _harness = new();

    public RootFilterResolverTests()
    {
        using var setup = _harness.CreateContext();
        setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        setup.AppSettings.Add(new AppSettings { Id = 1, ApiToken = "t", DefaultExtensionFilter = ["jpg"] });
        setup.Volumes.Add(new Volume
        {
            Id = VolumeId, VolumeGuid = @"\\?\Volume{r}\", FileSystem = "NTFS", IsOnline = true,
        });
        setup.SaveChanges();
    }

    public void Dispose() => _harness.Dispose();

    [Theory]
    [InlineData(@"Media\Foto\a.jpg", "Media\\Foto")]   // the deeper root wins
    [InlineData(@"Media\a.jpg", "Media")]
    [InlineData(@"media\foto\a.jpg", "Media\\Foto")]   // case-insensitive, like every path rule
    [InlineData(@"Mediateca\a.jpg", null)]             // segment-aware: not inside "Media"
    [InlineData(@"Altro\a.jpg", null)]
    public void MostSpecificRoot_picks_the_deepest_containing_root(string path, string? expected) =>
        RootFilterResolver.MostSpecificRoot([@"Media", @"Media\Foto"], path).Should().Be(expected);

    [Fact]
    public void The_volume_root_contains_everything()
        => RootFilterResolver.MostSpecificRoot([""], @"Anything\at\all").Should().Be("");

    [Fact]
    public async Task A_root_override_wins_over_the_global_default()
    {
        AddRoot("Media", """{ "extensions": ["mp4"] }""");

        var filter = await Resolve(@"Media\clip.mp4");

        filter.AllowedExtensions.Should().BeEquivalentTo("mp4");
    }

    [Fact]
    public async Task A_path_under_no_active_root_falls_back_to_the_global_default()
    {
        AddRoot("Media", """{ "extensions": ["mp4"] }""");

        var filter = await Resolve(@"Altrove\foto.jpg");

        // The widest sensible answer: pretending nothing is allowed would exclude rows the user
        // never asked to exclude.
        filter.AllowedExtensions.Should().BeEquivalentTo("jpg");
    }

    [Fact]
    public async Task An_inactive_root_does_not_govern_anything()
    {
        AddRoot("Media", """{ "extensions": ["mp4"] }""", isActive: false);

        (await Resolve(@"Media\clip.mp4")).AllowedExtensions.Should().BeEquivalentTo("jpg");
    }

    /// <summary>§9: not swallowed — logged, and the root recovers to the defaults so the caller still has an answer.</summary>
    [Fact]
    public async Task A_malformed_override_falls_back_to_the_defaults_instead_of_throwing()
    {
        AddRoot("Media", "{ not valid json");

        (await Resolve(@"Media\foto.jpg")).AllowedExtensions.Should().BeEquivalentTo("jpg");
    }

    private void AddRoot(string path, string? overrideJson, bool isActive = true)
    {
        using var db = _harness.CreateContext();
        db.WatchedRoots.Add(new WatchedRoot
        {
            VolumeId = VolumeId, RelativePath = path, IsActive = isActive, FilterOverrideJson = overrideJson,
        });
        db.SaveChanges();
    }

    private async Task<EffectiveFilter> Resolve(string path)
    {
        await using var db = _harness.CreateContext();
        return await TestProjection.Filters(db).ResolveForPathAsync(VolumeId, path, CancellationToken.None);
    }
}
