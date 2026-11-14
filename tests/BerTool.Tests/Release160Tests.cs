using BerTool.Server.Configuration;
using BerTool.Server.Endpoints;
using BerTool.Server.Storage;
using BerTool.Shared.Contracts;
using BerTool.Shared.Products;
using BerTool.TrayAgent.Reporting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BerTool.Tests;

/// <summary>1.6.0 hata düzeltmeleri ve yeni davranışlar için regresyon testleri.</summary>
public sealed class Release160Tests : IDisposable
{
    private const string MachineId = "0123456789abcdef0123456789abcdef";
    private const string OtherMachineId = "fedcba9876543210fedcba9876543210";

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

    // ---------- Servis eşleştirme ----------

    [Fact]
    public void ServisEslestirme_BirebirAdZayifEslesmedenOnceGelir()
    {
        // Görünen adında beklenen adı içeren durmuş bir servis listede önce
        // gelse bile asıl (çalışan) servis seçilmelidir.
        var product = new ProductDefinitionDto
        {
            Id = "bigfix",
            DisplayName = "Bigfix",
            ServiceNames = new List<string> { "BESClient" },
            MatchMode = ServiceMatchMode.All
        };
        var services = new List<ServiceSnapshotDto>
        {
            new() { Name = "AAAHelper", DisplayName = "BESClient Yardımcı Güncelleyici", Status = "Stopped" },
            new() { Name = "BESClient", DisplayName = "BES Client", Status = "Running" }
        };

        var status = ProductHealthEvaluator.Evaluate(product, services);

        Assert.True(status.IsHealthy, status.Details);
        Assert.Contains("BESClient", status.Details);
    }

    [Fact]
    public void ServisEslestirme_AyniDuzeydeCalisanServisTercihEdilir()
    {
        var product = new ProductDefinitionDto
        {
            Id = "zabbix",
            DisplayName = "Zabbix",
            ServiceNames = new List<string> { "ZabbixAgentService" },
            MatchMode = ServiceMatchMode.All
        };
        var services = new List<ServiceSnapshotDto>
        {
            new() { Name = "ZabbixAgentServiceOld", DisplayName = "Eski", Status = "Stopped" },
            new() { Name = "ZabbixAgentServiceNew", DisplayName = "Yeni", Status = "Running" }
        };

        Assert.True(ProductHealthEvaluator.Evaluate(product, services).IsHealthy);
    }

    // ---------- BitLocker kuralları ----------

    [Fact]
    public void BitLocker_KorumasiAskidaOlanDiskRisklidir()
    {
        Assert.True(BitLockerRules.IsHealthy("XTS-AES-256", true, "FullyEncrypted"));
        Assert.False(BitLockerRules.IsHealthy("XTS-AES-256", false, "FullyEncrypted"));
        Assert.False(BitLockerRules.IsHealthy("XTS-AES-256", true, "DecryptionInProgress"));
        Assert.False(BitLockerRules.IsHealthy("XTS-AES-128", true, "FullyEncrypted"));

        // Eski ajanlar koruma/dönüştürme bilgisini göndermez; cezalandırılmaz.
        Assert.True(BitLockerRules.IsHealthy("XTS-AES-256", null, string.Empty));
    }

    [Fact]
    public void BitLocker_RiskNedenleriAciklayicidir()
    {
        var reasons = BitLockerRules.EvaluateRiskReasons(new List<BitLockerVolumeDto>
        {
            new() { DriveLetter = "C:", EncryptionMethod = "XTS-AES-256", ProtectionOn = false, ConversionStatus = "FullyEncrypted" },
            new() { DriveLetter = "D:", EncryptionMethod = "XTS-AES-256", ProtectionOn = true, ConversionStatus = "EncryptionInProgress" }
        });

        Assert.Contains(reasons, reason => reason.StartsWith(BitLockerRules.ProtectionOffReasonPrefix) && reason.Contains("C:"));
        Assert.Contains(reasons, reason => reason.StartsWith(BitLockerRules.ConversionReasonPrefix) && reason.Contains("D:"));
        Assert.DoesNotContain(BitLockerRules.WeakMethodReason, reasons);
    }

    [Fact]
    public void BitLocker_OkunamayanDiskSifresizDiyeRaporlanmaz()
    {
        var unreadable = BitLockerRules.EvaluateRiskReasons(new List<BitLockerVolumeDto>
        {
            new() { DriveLetter = "C:", EncryptionMethod = "Unknown" }
        });
        Assert.Equal(new[] { BitLockerRules.UnreadableReason }, unreadable);

        var unencrypted = BitLockerRules.EvaluateRiskReasons(new List<BitLockerVolumeDto>
        {
            new() { DriveLetter = "C:", EncryptionMethod = "N/A" }
        });
        Assert.Equal(new[] { BitLockerRules.WeakMethodReason }, unencrypted);
    }

    [Fact]
    public void RaporTemizleyici_BitLockerSagliginiKorumaDurumundanHesaplar()
    {
        var report = CreateReport();
        report.BitLockerVolumes = new List<BitLockerVolumeDto>
        {
            // Ajan "sağlıklı" dese bile koruma askıdaysa sunucu riskli sayar.
            new() { DriveLetter = "C:", EncryptionMethod = "XTS-AES-256", ProtectionOn = false, IsHealthy = true }
        };

        var normalized = ReportSanitizer.Normalize(report);

        Assert.False(normalized.BitLockerVolumes[0].IsHealthy);
        Assert.False(normalized.BitLockerVolumes[0].ProtectionOn);
    }

    [Fact]
    public async Task Sunucu_AskidakiBitLockerKorumasiniRiskSayar_AjaninEskiMetniniTekrarlamaz()
    {
        var store = await CreateInitializedStoreAsync();
        var report = CreateReport();
        report.BitLockerVolumes = new List<BitLockerVolumeDto>
        {
            new() { DriveLetter = "C:", EncryptionMethod = "XTS-AES-256", ProtectionOn = false }
        };
        report.RiskReasons = new List<string> { "BitLocker eski ajan metni" };

        await store.SaveReportAsync(ReportSanitizer.Normalize(report));
        var detail = await store.GetMachineDetailAsync(MachineId);

        Assert.NotNull(detail);
        Assert.Equal("risky", detail!.Summary.Status);
        Assert.Contains(detail.Summary.RiskReasons, reason => reason.StartsWith(BitLockerRules.ProtectionOffReasonPrefix));
        Assert.DoesNotContain("BitLocker eski ajan metni", detail.Summary.RiskReasons);
    }

    // ---------- Ajan API anahtarı geçiş süresi ----------

    [Fact]
    public async Task AnahtarYenileme_GecisSuresindeEskiAnahtarKabulEdilirVeIsaretlenir()
    {
        var store = await CreateInitializedStoreAsync();
        var oldKey = await store.RotateAgentApiKeyAsync();

        // Bir makine yenilemeden önce raporlamış olsun.
        await store.SaveReportAsync(ReportSanitizer.Normalize(CreateReport()));

        var (newKey, expiresUtc) = await store.RotateAgentApiKeyAsync(keepPreviousKey: true, graceHours: 24);

        Assert.NotNull(expiresUtc);
        Assert.Equal(AgentKeyMatch.Current, store.MatchAgentApiKey(newKey));
        Assert.Equal(AgentKeyMatch.Previous, store.MatchAgentApiKey(oldKey));
        Assert.Equal(AgentKeyMatch.None, store.MatchAgentApiKey("yanlis-anahtar"));

        // Yenilemeden önce görülen makine "eski anahtar" sayılır.
        var settings = await store.GetAdminSettingsAsync();
        Assert.Equal(1, settings.MachinesUsingPreviousApiKey);
        Assert.NotNull(settings.PreviousAgentApiKeyExpiresUtc);

        // Eski anahtarla gelen rapor kabul edilir ama işaretli kalır.
        await store.SaveReportAsync(ReportSanitizer.Normalize(CreateReport()), keyMatch: AgentKeyMatch.Previous);
        Assert.True((await store.GetMachineDetailAsync(MachineId))!.Summary.UsesPreviousApiKey);

        // Yeni anahtarla gelince işaret kalkar.
        await store.SaveReportAsync(ReportSanitizer.Normalize(CreateReport()), keyMatch: AgentKeyMatch.Current);
        Assert.False((await store.GetMachineDetailAsync(MachineId))!.Summary.UsesPreviousApiKey);
        Assert.Equal(0, (await store.GetAdminSettingsAsync()).MachinesUsingPreviousApiKey);

        // Yönetici iptal edince eski anahtar hemen reddedilir.
        Assert.True(await store.RevokePreviousAgentApiKeyAsync());
        Assert.Equal(AgentKeyMatch.None, store.MatchAgentApiKey(oldKey));
        Assert.False(await store.RevokePreviousAgentApiKeyAsync());
    }

    [Fact]
    public async Task AnahtarYenileme_HemenIptalEskiAnahtariReddederVeKaliciOlur()
    {
        var store = await CreateInitializedStoreAsync();
        var oldKey = await store.RotateAgentApiKeyAsync();
        var (newKey, expiresUtc) = await store.RotateAgentApiKeyAsync(keepPreviousKey: false, graceHours: 168);

        Assert.Null(expiresUtc);
        Assert.Equal(AgentKeyMatch.None, store.MatchAgentApiKey(oldKey));

        // Yeniden başlatılan sunucu aynı sonucu verir.
        var reloaded = CreateStore();
        await reloaded.InitializeAsync();
        Assert.Equal(AgentKeyMatch.Current, reloaded.MatchAgentApiKey(newKey));
        Assert.Equal(AgentKeyMatch.None, reloaded.MatchAgentApiKey(oldKey));
    }

    [Fact]
    public async Task AnahtarYenileme_GecisSuresiYenidenBaslatmadaKorunur()
    {
        var store = await CreateInitializedStoreAsync();
        var oldKey = await store.RotateAgentApiKeyAsync();
        await store.RotateAgentApiKeyAsync(keepPreviousKey: true, graceHours: 48);

        var reloaded = CreateStore();
        await reloaded.InitializeAsync();

        Assert.Equal(AgentKeyMatch.Previous, reloaded.MatchAgentApiKey(oldKey));
    }

    // ---------- Veri deposu dayanıklılığı ----------

    [Fact]
    public async Task BozukServerJson_YedektenGeriYuklenir()
    {
        var store = await CreateInitializedStoreAsync();
        var apiKey = await store.RotateAgentApiKeyAsync();

        // Bir ayar değişikliği daha: server.json.bak artık anahtarlı sürümü tutar.
        var settings = await store.GetAdminSettingsAsync();
        await store.UpdateSettingsAsync(new UpdateAdminSettingsRequest
        {
            ReportIntervalSeconds = settings.ReportIntervalSeconds,
            OfflineThresholdMinutes = settings.OfflineThresholdMinutes,
            HistoryLimit = settings.HistoryLimit + 1
        });

        // Elektrik kesintisinde yarım kalmış yazmayı taklit et.
        var serverJson = Path.Combine(_dataDirectory, "server.json");
        File.WriteAllText(serverJson, "{ \"adminUsername\": \"adm");

        var reloaded = CreateStore();
        await reloaded.InitializeAsync();

        Assert.True(reloaded.ValidateAgentApiKey(apiKey));
        Assert.True(File.Exists(serverJson));
        Assert.Contains(Directory.EnumerateFiles(_dataDirectory), path => Path.GetFileName(path).StartsWith("server.json.bozuk-"));

        // Geri yüklenen dosya sağlam olmalı; bir sonraki açılış normal yoldan okur.
        var third = CreateStore();
        await third.InitializeAsync();
        Assert.True(third.ValidateAgentApiKey(apiKey));
    }

    [Fact]
    public async Task YarimKalanGeciciDosyalar_BaslangictaTemizlenir()
    {
        var store = await CreateInitializedStoreAsync();
        await store.SaveReportAsync(ReportSanitizer.Normalize(CreateReport()));

        var leftover = Path.Combine(_dataDirectory, "machines", $"{OtherMachineId}.json.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(leftover, "yarım");

        var reloaded = CreateStore();
        await reloaded.InitializeAsync();

        Assert.False(File.Exists(leftover));
        Assert.NotNull(await reloaded.GetMachineDetailAsync(MachineId));
    }

    // ---------- AD ayarları ----------

    [Fact]
    public void AdParmakIzi_ZamanAsimiVeYedekGirisDegisinceDegismez()
    {
        var options = new AdOptions
        {
            Enabled = true,
            Host = "dc01.ornek.local",
            BaseDn = "DC=ornek,DC=local",
            AdminGroup = "BerTool Admins",
            TimeoutSeconds = 10,
            AllowLocalFallback = true
        };

        var first = options.CreateFingerprint();
        options.TimeoutSeconds = 30;
        options.AllowLocalFallback = false;

        Assert.Equal(first, options.CreateFingerprint());
    }

    [Fact]
    public async Task AdKapatma_SifrelemeSecimindenBagimsizKaydedilir()
    {
        var store = await CreateInitializedStoreAsync();

        await store.UpdateAdSettingsAsync(new AdSettingsUpdateRequest
        {
            Enabled = false,
            UseSsl = false,
            UseStartTls = false
        });

        Assert.False(store.GetAdStatus().Enabled);

        await Assert.ThrowsAsync<StoreValidationException>(() => store.UpdateAdSettingsAsync(new AdSettingsUpdateRequest
        {
            Enabled = true,
            Host = "dc01.ornek.local",
            UseSsl = false,
            UseStartTls = false
        }));
    }

    // ---------- Ajan zamanlaması ----------

    [Fact]
    public void RaporZamani_SaatGeriAlinincaBeklemez()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var interval = TimeSpan.FromMinutes(5);

        Assert.False(ReportSchedule.IsDue(now, now.AddMinutes(3), interval));
        Assert.True(ReportSchedule.IsDue(now, now.AddMinutes(-1), interval));

        // Saat bir gün geri alındı: planlanan zaman aralıktan çok uzakta.
        Assert.True(ReportSchedule.IsDue(now, now.AddDays(1), interval));
    }

    private ServerStore CreateStore()
    {
        var options = new ServerOptions { DataDirectory = _dataDirectory };
        return new ServerStore(options, _dataDirectory, NullLogger<ServerStore>.Instance);
    }

    private async Task<ServerStore> CreateInitializedStoreAsync()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        return store;
    }

    private static AgentReportDto CreateReport() => new()
    {
        AgentVersion = "1.6.0",
        MachineId = MachineId,
        MachineName = "TEST-PC",
        DomainOrWorkgroup = "WORKGROUP",
        LoggedInUser = "test",
        OperatingSystem = "Windows",
        ComputerModel = "Test",
        Services = new List<ServiceSnapshotDto>(),
        BitLockerVolumes = new List<BitLockerVolumeDto>
        {
            new() { DriveLetter = "C:", EncryptionMethod = "XTS-AES-256", ProtectionOn = true, ConversionStatus = "FullyEncrypted" }
        },
        CollectedAtUtc = DateTimeOffset.UtcNow,
        CheckedAtUtc = DateTimeOffset.UtcNow
    };
}
