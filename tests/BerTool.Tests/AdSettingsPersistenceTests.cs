using BerTool.Server.Configuration;
using BerTool.Server.Security;
using BerTool.Server.Storage;
using BerTool.Shared.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BerTool.Tests;

public sealed class AdSettingsPersistenceTests : IDisposable
{
    private const string BindPassword = "Gizli-Parola-123!";

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        "BerTool.Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch
        {
            // Test temizliği başarısız olsa da sonuç etkilenmez.
        }
    }

    [Fact]
    public async Task AdAyarlari_KaydedilirVeParolaDiskeDuzMetinYazilmaz()
    {
        var protector = new EphemeralDataProtectionProvider();
        var store = CreateStore(protector);
        await store.InitializeAsync();

        await store.UpdateAdSettingsAsync(CreateRequest());

        Assert.Equal(BindPassword, store.GetAdOptions().BindPassword);
        Assert.True(store.GetAdStatus().BindAccountConfigured);

        var json = await File.ReadAllTextAsync(Path.Combine(_dataDirectory, "server.json"));
        Assert.DoesNotContain(BindPassword, json);
        Assert.Contains("protectedBindPassword", json);

        // Aynı Data Protection anahtarlarıyla yeni örnek parolayı çözebilmelidir.
        var reloaded = CreateStore(protector);
        await reloaded.InitializeAsync();

        Assert.True(reloaded.GetAdStatus().Enabled);
        Assert.True(reloaded.GetAdStatus().BindAccountConfigured);
        Assert.Equal(BindPassword, reloaded.GetAdOptions().BindPassword);
        Assert.Equal("dc01.ornek.local", reloaded.GetAdOptions().Host);
    }

    [Fact]
    public async Task BosParola_KayitliParolayiKorur()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpdateAdSettingsAsync(CreateRequest());

        var request = CreateRequest();
        request.Host = "dc02.ornek.local";
        request.BindPassword = string.Empty;

        await store.UpdateAdSettingsAsync(request);

        Assert.Equal("dc02.ornek.local", store.GetAdOptions().Host);
        Assert.Equal(BindPassword, store.GetAdOptions().BindPassword);

        var json = await File.ReadAllTextAsync(Path.Combine(_dataDirectory, "server.json"));
        Assert.DoesNotContain(BindPassword, json);
    }

    [Fact]
    public async Task GecersizAdAyarı_KayitliAyarlariDegistirmez()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpdateAdSettingsAsync(CreateRequest());

        var invalid = CreateRequest();
        invalid.Host = "dc02.ornek.local";
        invalid.UseSsl = false;
        invalid.UseStartTls = false;

        await Assert.ThrowsAsync<StoreValidationException>(
            () => store.UpdateAdSettingsAsync(invalid));

        Assert.Equal("dc01.ornek.local", store.GetAdOptions().Host);
        Assert.True(store.GetAdStatus().Configured);
    }

    [Fact]
    public async Task AdAyariDegisince_EskiAdOturumuGecersizOlur()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpdateAdSettingsAsync(CreateRequest());

        var service = new AdAuthenticationService(
            store,
            NullLogger<AdAuthenticationService>.Instance);
        var previousFingerprint = service.Fingerprint;

        Assert.True(service.IsSessionAllowed("ad", previousFingerprint));

        var changed = CreateRequest();
        changed.AdminGroup = "Baska Yonetici Grubu";
        await store.UpdateAdSettingsAsync(changed);

        Assert.False(service.IsSessionAllowed("ad", previousFingerprint));
        Assert.True(service.IsSessionAllowed("ad", service.Fingerprint));
    }

    private ServerStore CreateStore(IDataProtectionProvider? protector = null)
    {
        var options = new ServerOptions { DataDirectory = _dataDirectory };
        return new ServerStore(
            options,
            _dataDirectory,
            NullLogger<ServerStore>.Instance,
            protector ?? new EphemeralDataProtectionProvider());
    }

    private static AdSettingsUpdateRequest CreateRequest() => new()
    {
        Enabled = true,
        Host = "dc01.ornek.local",
        Port = 0,
        UseSsl = true,
        UseStartTls = false,
        BaseDn = "DC=ornek,DC=local",
        BindDn = "CN=svc,DC=ornek,DC=local",
        BindPassword = BindPassword,
        AdminGroup = "BerTool Admins",
        AllowLocalFallback = true,
        TimeoutSeconds = 5
    };
}
