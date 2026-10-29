using BerTool.Server.Configuration;
using BerTool.Server.Security;
using BerTool.Server.Storage;
using BerTool.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace BerTool.Tests;

public sealed class ServerStoreTests : IDisposable
{
    private const string MachineId = "0123456789abcdef0123456789abcdef";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

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
    public async Task AyniAyarlar_SurumArtirmaz()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var before = await store.GetAdminSettingsAsync();
        var after = await store.UpdateSettingsAsync(new UpdateAdminSettingsRequest
        {
            ReportIntervalSeconds = before.ReportIntervalSeconds,
            OfflineThresholdMinutes = before.OfflineThresholdMinutes,
            HistoryLimit = before.HistoryLimit
        });

        Assert.Equal(before.ConfigVersion, after.ConfigVersion);
    }

    [Fact]
    public async Task AyarDegisikligi_SurumArtirirVeGecmisiHemenKirpar()
    {
        WriteMachineWithHistory(_dataDirectory, MachineId, historyCount: 20);

        var store = CreateStore();
        await store.InitializeAsync();

        var before = await store.GetAdminSettingsAsync();
        var updated = await store.UpdateSettingsAsync(new UpdateAdminSettingsRequest
        {
            ReportIntervalSeconds = before.ReportIntervalSeconds,
            OfflineThresholdMinutes = before.OfflineThresholdMinutes,
            HistoryLimit = 5
        });

        Assert.Equal(before.ConfigVersion + 1, updated.ConfigVersion);

        var detail = await store.GetMachineDetailAsync(MachineId);
        Assert.NotNull(detail);
        Assert.Equal(5, detail!.History.Count);

        // Değişiklik diske de yazılmalı; yeni örnek aynı sınırla açılmalı.
        var reloaded = CreateStore();
        await reloaded.InitializeAsync();
        var reloadedDetail = await reloaded.GetMachineDetailAsync(MachineId);
        Assert.NotNull(reloadedDetail);
        Assert.Equal(5, reloadedDetail!.History.Count);
    }

    [Fact]
    public async Task AyniSaniyeIcindeGelenRaporlar_TekGecmisKaydiTutar()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SaveReportAsync(CreateReport(DateTimeOffset.UtcNow));
        await store.SaveReportAsync(CreateReport(DateTimeOffset.UtcNow));

        var detail = await store.GetMachineDetailAsync(MachineId);
        Assert.NotNull(detail);
        Assert.Single(detail!.History);
    }

    [Fact]
    public async Task NullAlanliKayitlar_YuklenirkenHataVermez()
    {
        var machinesDirectory = Path.Combine(_dataDirectory, "machines");
        Directory.CreateDirectory(machinesDirectory);

        var machine = new StoredMachine
        {
            MachineId = MachineId,
            LatestReport = new AgentReportDto
            {
                MachineId = MachineId,
                MachineName = null!,
                Services = null!,
                PhysicalDiskDetails = null!,
                IpAddresses = null!,
                BitLockerVolumes = null!,
                RiskReasons = null!
            },
            History = null!
        };

        await File.WriteAllTextAsync(
            Path.Combine(machinesDirectory, $"{MachineId}.json"),
            JsonSerializer.Serialize(machine, JsonOptions));

        var store = CreateStore();
        await store.InitializeAsync();

        var detail = await store.GetMachineDetailAsync(MachineId);
        Assert.NotNull(detail);
        Assert.Equal("Bilinmiyor", detail!.Summary.MachineName);
        Assert.Empty(detail.History);
    }

    [Fact]
    public async Task TekrarlananUrunKimlikleri_YuklenirkenTemizlenir()
    {
        var data = new StoredServerData
        {
            AdminUsername = "admin",
            AdminPassword = PasswordHasher.Hash("Guclu-Sifre-123"),
            AgentApiKeyHash = PasswordHasher.HashAgentApiKey("ajan-anahtari"),
            Products = new List<ProductDefinitionDto>
            {
                new() { Id = "bigfix", DisplayName = "Bigfix", ServiceNames = new() { "BESClient" } },
                new() { Id = "BIGFIX", DisplayName = "Kopya", ServiceNames = new() { "BESClient" } },
                new() { Id = "   ", DisplayName = "Boş", ServiceNames = null! }
            }
        };

        Directory.CreateDirectory(_dataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(_dataDirectory, "server.json"),
            JsonSerializer.Serialize(data, JsonOptions));

        var store = CreateStore();
        await store.InitializeAsync();

        var products = await store.GetProductsAsync();
        Assert.Single(products);
        Assert.Equal("bigfix", products[0].Id);
    }

    [Fact]
    public async Task ZayifBitLockerYontemi_AjanSaglikliDeseBileRiskliSayilir()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var report = CreateReport(DateTimeOffset.UtcNow);
        report.BitLockerVolumes = new List<BitLockerVolumeDto>
        {
            // Eski veya değiştirilmiş bir ajan bayrağı yanlış gönderebilir.
            new() { DriveLetter = "C:", EncryptionMethod = "AES-CBC-128", IsHealthy = true }
        };
        report.RiskReasons = new List<string>();
        report.IsHealthy = true;

        await store.SaveReportAsync(report);

        var detail = await store.GetMachineDetailAsync(MachineId);
        Assert.NotNull(detail);
        Assert.False(detail!.Summary.IsHealthy);
        Assert.Contains("XTS-AES-256 dışındaki şifreleme tespit edildi.", detail.Summary.RiskReasons);
    }

    [Fact]
    public async Task BitLockerUygulanamayanCihaz_BitLockerRiskiUretmez()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var report = CreateReport(DateTimeOffset.UtcNow);
        report.IsBitLockerApplicable = false;
        report.BitLockerVolumes = new List<BitLockerVolumeDto>();

        await store.SaveReportAsync(report);

        var detail = await store.GetMachineDetailAsync(MachineId);
        Assert.NotNull(detail);
        Assert.DoesNotContain(detail!.Summary.RiskReasons, reason => reason.Contains("BitLocker") || reason.Contains("XTS"));
    }

    [Fact]
    public async Task IlkKurulumUrunleri_GuncellemeZamaniTasir()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var products = await store.GetProductsAsync();
        Assert.NotEmpty(products);
        Assert.All(products, product => Assert.True(product.UpdatedAtUtc > DateTimeOffset.UnixEpoch));
    }

    private ServerStore CreateStore()
    {
        var options = new ServerOptions { DataDirectory = _dataDirectory };
        return new ServerStore(options, _dataDirectory, NullLogger<ServerStore>.Instance);
    }

    private static AgentReportDto CreateReport(DateTimeOffset collectedAtUtc) => new()
    {
        AgentVersion = "1.3.1",
        MachineId = MachineId,
        MachineName = "TEST-PC",
        DomainOrWorkgroup = "WORKGROUP",
        LoggedInUser = "test",
        OperatingSystem = "Windows",
        ComputerModel = "Test",
        CpuModel = "Test CPU",
        PhysicalCoreCount = 4,
        LogicalProcessorCount = 8,
        TotalRamGb = 16,
        TotalDiskGb = 512,
        Services = new List<ServiceSnapshotDto>(),
        BitLockerVolumes = new List<BitLockerVolumeDto>
        {
            new() { DriveLetter = "C:", EncryptionMethod = "XTS-AES-256", IsHealthy = true }
        },
        CollectedAtUtc = collectedAtUtc,
        CheckedAtUtc = collectedAtUtc
    };

    private static void WriteMachineWithHistory(string dataDirectory, string machineId, int historyCount)
    {
        var machinesDirectory = Path.Combine(dataDirectory, "machines");
        Directory.CreateDirectory(machinesDirectory);

        var now = DateTimeOffset.UtcNow;
        var machine = new StoredMachine
        {
            MachineId = machineId,
            LatestReport = CreateReport(now),
            FirstSeenUtc = now.AddDays(-1),
            LastSeenUtc = now,
            History = Enumerable.Range(0, historyCount)
                .Select(index => new MachineHistoryEntryDto
                {
                    CollectedAtUtc = now.AddMinutes(-historyCount + index),
                    ReceivedAtUtc = now.AddMinutes(-historyCount + index),
                    IsHealthy = true,
                    ConfigVersion = 1
                })
                .ToList()
        };

        File.WriteAllText(
            Path.Combine(machinesDirectory, $"{machineId}.json"),
            JsonSerializer.Serialize(machine, JsonOptions));
    }
}
