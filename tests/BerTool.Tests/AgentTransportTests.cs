using BerTool.TrayAgent.Reporting;
using Xunit;

namespace BerTool.Tests;

public sealed class AgentTransportTests
{
    [Theory]
    [InlineData("https://bertool.example.com", true)]
    [InlineData("https://sunucu.ornek.local:5443", true)]
    [InlineData("http://127.0.0.1:5081", true)]
    [InlineData("http://localhost:5081", true)]
    [InlineData("http://[::1]:5081", true)]
    [InlineData("http://192.168.1.50:5081", false)]
    [InlineData("http://bertool.example.com", false)]
    [InlineData("http://10.0.0.5", false)]
    [InlineData("ftp://bertool.example.com", false)]
    public void AktarimKurali_SadeceGuvenliAdreslereIzinVerir(string url, bool expected)
    {
        var uri = new Uri(url);

        Assert.Equal(expected, AgentSettings.IsAllowedTransport(uri));
    }

    [Fact]
    public void UzakHttpAdresi_YapilandirilmisSayilmaz()
    {
        var settings = new AgentSettings
        {
            Enabled = true,
            ServerUrl = "http://192.168.1.50:5081",
            ApiKey = "test-anahtari"
        };

        Assert.False(settings.IsConfigured);
    }

    [Fact]
    public void YerelHttpAdresi_YapilandirilmisSayilir()
    {
        var settings = new AgentSettings
        {
            Enabled = true,
            ServerUrl = "http://127.0.0.1:5081",
            ApiKey = "test-anahtari"
        };

        Assert.True(settings.IsConfigured);
    }

    [Fact]
    public void HttpsAdresi_YapilandirilmisSayilir()
    {
        var settings = new AgentSettings
        {
            Enabled = true,
            ServerUrl = "https://bertool.example.com",
            ApiKey = "test-anahtari"
        };

        Assert.True(settings.IsConfigured);
    }

    [Fact]
    public void AjanApiIstemcisi_GuvenliHandlerAyarlariylaOlusturulur()
    {
        using var client = new AgentApiClient(new AgentSettings
        {
            Enabled = true,
            ServerUrl = "https://bertool.example.com",
            ApiKey = "test-anahtari"
        });
    }
}
