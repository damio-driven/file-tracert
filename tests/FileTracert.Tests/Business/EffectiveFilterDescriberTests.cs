using FileTracert.Business.Filtering;
using FileTracert.Contracts.Enums;
using FluentAssertions;

namespace FileTracert.Tests.Business;

public sealed class EffectiveFilterDescriberTests
{
    private static readonly Dictionary<string, FileCategory> Map = new()
    {
        ["jpg"] = FileCategory.Image,
        ["cr3"] = FileCategory.Image,
        ["mp4"] = FileCategory.Video,
    };

    [Fact]
    public void Collapses_extensions_to_distinct_categories_in_enum_order()
    {
        var filter = new EffectiveFilter(
            new HashSet<string> { "mp4", "jpg", "cr3" },
            ["$Recycle.Bin"]);

        var text = EffectiveFilterDescriber.Describe(filter, Map);

        text.Should().Be("Immagini, Video · esclude System, Hidden, $Recycle.Bin");
    }

    [Fact]
    public void Empty_allow_list_means_all_types()
    {
        var filter = new EffectiveFilter(new HashSet<string>(), [], ExcludeSystem: false, ExcludeHidden: false);

        EffectiveFilterDescriber.Describe(filter, Map).Should().Be("Tutti i tipi");
    }

    [Fact]
    public void Unknown_extension_falls_back_to_other()
    {
        var filter = new EffectiveFilter(new HashSet<string> { "xyz" }, [], ExcludeSystem: false, ExcludeHidden: false);

        EffectiveFilterDescriber.Describe(filter, Map).Should().Be("Altri");
    }
}
