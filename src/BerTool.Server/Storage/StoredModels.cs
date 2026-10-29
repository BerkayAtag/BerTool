using BerTool.Shared.Products;
using BerTool.Server.Security;
using BerTool.Shared.Contracts;

namespace BerTool.Server.Storage;

internal sealed class StoredServerData
{
    public string AdminUsername { get; set; } = "admin";
    public PasswordHashRecord AdminPassword { get; set; } = new();
    public string AgentApiKeyHash { get; set; } = string.Empty;
    public int ReportIntervalSeconds { get; set; } = DefaultProductCatalog.DefaultReportIntervalSeconds;
    public int OfflineThresholdMinutes { get; set; } = DefaultProductCatalog.DefaultOfflineThresholdMinutes;
    public int HistoryLimit { get; set; } = DefaultProductCatalog.DefaultHistoryLimit;
    public int ConfigVersion { get; set; } = DefaultProductCatalog.DefaultConfigVersion;
    public int SessionVersion { get; set; } = 1;
    public List<ProductDefinitionDto> Products { get; set; } = DefaultProductCatalog.Create().ToList();

    /// <summary>
    /// Yönetim konsolundan yönetilen Active Directory ayarları. Hizmet hesabı
    /// parolası düz metin olarak saklanmaz; ASP.NET Core Data Protection ile
    /// şifrelenmiş değer tutulur.
    /// </summary>
    public StoredAdSettings? Ad { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class StoredAdSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool UseSsl { get; set; } = true;
    public bool UseStartTls { get; set; }

    // IgnoreCertificateErrors ve AllowInsecureLdap bilinçli olarak burada
    // saklanmaz; yalnızca appsettings.json / ortam değişkenlerinden okunur.

    public string BaseDn { get; set; } = string.Empty;
    public string BindDn { get; set; } = string.Empty;

    /// <summary>Data Protection ile korunmuş hizmet hesabı parolası.</summary>
    public string ProtectedBindPassword { get; set; } = string.Empty;

    public string AdminGroup { get; set; } = string.Empty;
    public bool AllowLocalFallback { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 10;
}

internal sealed class StoredMachine
{
    public string MachineId { get; set; } = string.Empty;
    public AgentReportDto LatestReport { get; set; } = new();
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public List<MachineHistoryEntryDto> History { get; set; } = new();
}
