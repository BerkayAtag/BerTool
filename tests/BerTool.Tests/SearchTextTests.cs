using BerTool.Server.Storage;
using Xunit;

namespace BerTool.Tests;

public sealed class SearchTextTests
{
    [Theory]
    [InlineData("İSTANBUL-PC", "istanbul")]
    [InlineData("istanbul-pc", "İSTANBUL")]
    [InlineData("ISPARTA-01", "isparta")]
    [InlineData("ISPARTA-01", "ısparta")]
    [InlineData("ışık.çelik", "ISIK.CELIK")]
    [InlineData("Defender Antivirüs", "antivirus")]
    [InlineData("Güvenlik Şubesi", "guvenlik subesi")]
    [InlineData("IKEEXT", "ikeext")]
    public void Contains_TurkceHarfVeAksanFarkiniYokSayar(string value, string term)
    {
        Assert.True(SearchText.Contains(value, SearchText.Fold(term)));
    }

    [Fact]
    public void Contains_EslesmeyenTerimiBulmaz()
    {
        Assert.False(SearchText.Contains("DESKTOP-01", SearchText.Fold("laptop")));
        Assert.False(SearchText.Contains(null, SearchText.Fold("x")));
        Assert.False(SearchText.Contains(string.Empty, SearchText.Fold("x")));
    }

    [Fact]
    public void Fold_GecersizUnicodeDizisindeHataVermez()
    {
        var invalid = "abc" + '\uD800' + "def";

        var folded = SearchText.Fold(invalid);

        Assert.Contains("abc", folded);
        Assert.Contains("def", folded);
    }
}
