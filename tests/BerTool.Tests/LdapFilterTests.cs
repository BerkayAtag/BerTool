using BerTool.Server.Security;
using Xunit;

namespace BerTool.Tests;

public sealed class LdapFilterTests
{
    [Fact]
    public void Escape_Rfc4515OzelKarakterleriniKacisir()
    {
        Assert.Equal(@"\2a\28\29\5c\00", LdapFilter.Escape("*()\\\0"));
    }

    [Fact]
    public void Escape_NormalKullaniciAdiniDegistirmez()
    {
        Assert.Equal("berkay.atag", LdapFilter.Escape("berkay.atag"));
        Assert.Equal(string.Empty, LdapFilter.Escape(null));
    }
}
