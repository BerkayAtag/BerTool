using BerTool.Server.Configuration;
using BerTool.Server.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BerTool.Tests;

public sealed class AdAuthenticationServiceTests
{
    [Fact]
    public void AdKapaliyken_YerelOturumGecerlidir()
    {
        var service = CreateService(new AdOptions { Enabled = false });

        Assert.True(service.IsLocalLoginEnabled);
        Assert.True(service.IsSessionAllowed("local", null));
        Assert.True(service.IsSessionAllowed(null, null));
        Assert.False(service.IsSessionAllowed("ad", null));
    }

    [Fact]
    public void YerelYedekKapaliyken_YerelOturumReddedilir()
    {
        var service = CreateService(new AdOptions
        {
            Enabled = true,
            Host = "dc01.ornek.local",
            BaseDn = "DC=ornek,DC=local",
            BindDn = "CN=svc,DC=ornek,DC=local",
            BindPassword = "parola",
            AdminGroup = "BerTool Admins",
            AllowLocalFallback = false
        });

        Assert.False(service.IsLocalLoginEnabled);
        Assert.False(service.IsSessionAllowed("local", null));
        Assert.False(service.IsSessionAllowed(null, null));
    }

    [Fact]
    public void AdOturumu_ParmakIziEslesirseGecerlidir()
    {
        var options = new AdOptions
        {
            Enabled = true,
            Host = "dc01.ornek.local",
            BaseDn = "DC=ornek,DC=local",
            BindDn = "CN=svc,DC=ornek,DC=local",
            BindPassword = "parola",
            AdminGroup = "BerTool Admins"
        };

        var service = CreateService(options);

        Assert.True(service.IsSessionAllowed("ad", service.Fingerprint));
        Assert.False(service.IsSessionAllowed("ad", "BASKA-PARMAK-IZI"));
    }

    private static AdAuthenticationService CreateService(AdOptions adOptions)
    {
        var serverOptions = new ServerOptions { Ad = adOptions };
        return new AdAuthenticationService(serverOptions, NullLogger<AdAuthenticationService>.Instance);
    }
}
