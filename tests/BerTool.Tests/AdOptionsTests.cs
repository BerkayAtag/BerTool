using BerTool.Server.Configuration;
using Xunit;

namespace BerTool.Tests;

public sealed class AdOptionsTests
{
    [Fact]
    public void DisabledYapilandirma_Gecerlidir()
    {
        var options = new AdOptions();

        Assert.Null(options.Validate());
        Assert.True(options.ToStatus().Configured);
        Assert.False(options.ToStatus().Enabled);
    }

    [Fact]
    public void EtkinYapilandirma_ZorunluAlanlariDogrular()
    {
        var options = new AdOptions { Enabled = true };

        Assert.Contains("Host", options.Validate());

        options.Host = "dc01.ornek.local";
        Assert.Contains("BaseDn", options.Validate());

        options.BaseDn = "DC=ornek,DC=local";
        Assert.Contains("BindDn", options.Validate());

        options.BindDn = "CN=svc,DC=ornek,DC=local";
        options.BindPassword = "parola";
        Assert.Contains("AdminGroup", options.Validate());

        options.AdminGroup = "BerTool Admins";
        Assert.Null(options.Validate());
    }

    [Fact]
    public void SifresizLdap_VarsayilanOlarakReddedilir()
    {
        var options = new AdOptions
        {
            Enabled = true,
            Host = "dc01.ornek.local",
            UseSsl = false,
            UseStartTls = false,
            BaseDn = "DC=ornek,DC=local",
            BindDn = "CN=svc,DC=ornek,DC=local",
            BindPassword = "parola",
            AdminGroup = "BerTool Admins"
        };

        Assert.Contains("AllowInsecureLdap", options.Validate());

        options.AllowInsecureLdap = true;
        Assert.Null(options.Validate());
    }

    [Fact]
    public void PortVeSifrelemeAdi_DogruHesaplanir()
    {
        var options = new AdOptions { UseSsl = true };
        Assert.Equal(636, options.EffectivePort);
        Assert.Equal("LDAPS", options.EncryptionName);

        options.UseSsl = false;
        options.UseStartTls = true;
        Assert.Equal(389, options.EffectivePort);
        Assert.Equal("StartTLS", options.EncryptionName);

        options.UseStartTls = false;
        Assert.Equal("Şifresiz LDAP", options.EncryptionName);
    }

    [Fact]
    public void ParmakIzi_YapilandirmaDegisinceDegisir()
    {
        var options = new AdOptions
        {
            Enabled = true,
            Host = "dc01.ornek.local",
            BaseDn = "DC=ornek,DC=local",
            AdminGroup = "BerTool Admins"
        };

        var first = options.CreateFingerprint();
        Assert.Equal(first, options.CreateFingerprint());

        options.AdminGroup = "Baska Grup";
        Assert.NotEqual(first, options.CreateFingerprint());
    }
}
