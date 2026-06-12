using FileTracert.Contracts.Platform;
using FluentAssertions;

namespace FileTracert.Tests.Business;

public class FrnUtilTests
{
    [Theory]
    [InlineData(0x0005000000000005ul, 5ul)]
    [InlineData(0x0000000000000005ul, 5ul)]
    [InlineData(0x1234_0000_0000_000Aul, 0x0Aul)]
    public void MftIndex_strips_the_sequence_number(ulong frn, ulong expectedIndex)
    {
        FrnUtil.MftIndex(frn).Should().Be(expectedIndex);
    }

    [Theory]
    [InlineData(0x0005000000000005ul, true)]  // root with sequence number
    [InlineData(0x0000000000000005ul, true)]  // bare index 5
    [InlineData(0x0005000000000006ul, false)] // index 6
    public void IsRoot_matches_index_5_regardless_of_sequence(ulong frn, bool expected)
    {
        FrnUtil.IsRoot(frn).Should().Be(expected);
    }
}
