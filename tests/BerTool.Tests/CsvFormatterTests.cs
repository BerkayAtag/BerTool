using BerTool.Server.Endpoints;
using BerTool.Server.Security;
using Xunit;

namespace BerTool.Tests;

public sealed class CsvFormatterTests
{
    [Theory]
    [InlineData("=cmd|' /C calc'!A0")]
    [InlineData("+SUM(1,1)")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tformul")]
    [InlineData("\rformul")]
    public void Escape_FormulOnekleriniEtkisizlestirir(string value)
    {
        var result = CsvFormatter.Escape(value);

        Assert.StartsWith("\"'", result);
    }

    [Fact]
    public void Escape_CiftTirnaklariIkiKatinaCikarir()
    {
        var result = CsvFormatter.Escape("Makine \"A\"");

        Assert.Equal("\"Makine \"\"A\"\"\"", result);
    }

    [Fact]
    public void Escape_NormalDegeriKorur()
    {
        Assert.Equal("\"DESKTOP-01\"", CsvFormatter.Escape("DESKTOP-01"));
        Assert.Equal("\"\"", CsvFormatter.Escape(null));
    }
}
