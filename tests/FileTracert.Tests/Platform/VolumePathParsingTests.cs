using FileTracert.Platform.Internal;
using FluentAssertions;

namespace FileTracert.Tests.Platform;

public class VolumePathParsingTests
{
    [Theory]
    [InlineData(@"\\?\Volume{12345678-1234-1234-1234-1234567890ab}\", true)]
    [InlineData(@"\\?\Volume{B75E2C83-0000-0000-0000-602F00000000}\", true)]
    [InlineData(@"\\?\Volume{12345678-1234-1234-1234-1234567890ab}", false)] // missing trailing slash
    [InlineData(@"C:\", false)]
    [InlineData(@"\\?\Volume{not-a-guid}\", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsVolumeGuidPath_validates_format(string? value, bool expected)
    {
        VolumePathParsing.IsVolumeGuidPath(value).Should().Be(expected);
    }

    [Fact]
    public void SplitDoubleNullTerminated_returns_each_nonempty_entry()
    {
        var buffer = "C:\\\0D:\\\0\0".ToCharArray();

        var result = VolumePathParsing.SplitDoubleNullTerminated(buffer);

        result.Should().Equal("C:\\", "D:\\");
    }

    [Fact]
    public void SplitDoubleNullTerminated_empty_buffer_returns_empty()
    {
        var buffer = "\0\0".ToCharArray();

        VolumePathParsing.SplitDoubleNullTerminated(buffer).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0x1A2B3C4Du, "1A2B-3C4D")]
    [InlineData(0x00000000u, "0000-0000")]
    [InlineData(0xFFFFFFFFu, "FFFF-FFFF")]
    public void FormatSerial_uses_high_low_word_form(uint serial, string expected)
    {
        VolumePathParsing.FormatSerial(serial).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\", "C:")]
    [InlineData(@"d:\", "D:")]
    [InlineData(@"C:\mount\folder\", "C:")]
    [InlineData(@"\\?\Volume{x}\", null)] // folder/volume mount, not a drive letter
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DriveLetterKey_extracts_uppercase_letter(string? mountPoint, string? expected)
    {
        VolumePathParsing.DriveLetterKey(mountPoint).Should().Be(expected);
    }
}
