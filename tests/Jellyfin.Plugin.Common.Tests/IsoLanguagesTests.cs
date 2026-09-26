using Jellyfin.Plugin.Common.Languages;
using Xunit;

namespace Jellyfin.Plugin.Common.Tests;

// FAM-01: language codes without the server's culture data
public class IsoLanguagesTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("eng", "en")]
    [InlineData("English", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("swe", "sv")]
    [InlineData("SV", "sv")]
    [InlineData("ger", "de")]
    [InlineData("deu", "de")]
    [InlineData("chi", "zh")]
    [InlineData("pt_BR", "pt")]
    [InlineData("heb", "he")]
    [InlineData("klingon", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Codes_and_names_map_to_two_letters(string? value, string? two)
        => Assert.Equal(two, IsoLanguages.TwoLetter(value));

    [Fact]
    public void Three_letter_codes_and_names_come_back()
    {
        Assert.Equal("swe", IsoLanguages.ThreeLetter("sv"));
        Assert.Equal("fra", IsoLanguages.ThreeLetter("fre"));
        Assert.Equal("Swedish", IsoLanguages.EnglishName("swe"));
    }
}
