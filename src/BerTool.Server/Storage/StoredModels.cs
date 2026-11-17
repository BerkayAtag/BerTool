using BerTool.Shared.Products;
using BerTool.Server.Security;
using BerTool.Shared.Contracts;
using System.Text.Json.Serialization;

namespace BerTool.Server.Storage;

internal sealed class StoredServerData
{
    public string AdminUsername { get; set; } = "admin";
    public PasswordHashRecord AdminPassword { get; set; } = new();
    public string AgentApiKeyHash { get; set; } = string.Empty;

    /// <summary>
    /// Anahtar yenilendikten sonra geçiş süresi boyunca kabul edilen önceki
    /// anahtarlar; her birinin kendi bitişi vardır. Liste değiştirilmez, her
    /// değişiklikte yenisiyle değiştirilir (kilitsiz okuyucular güvenle gezer).
    /// Birden çok kayıt tutulur: geçiş süresi içinde anahtar ikinci kez
    /// yenilendiğinde (ör. yeni anahtar kopyalanmadan kaybolduğunda) hâlâ
    /// eski anahtarı kullanan filo kopmaz.
    /// </summary>
    public List<StoredPreviousAgentKey> PreviousAgentApiKeys { get; set; } = new();

    /// <summary>Anahtarın son yenilendiği an. Bundan önce görülen makineler eski anahtarı kullanıyordur.</summary>
    public DateTimeOffset? AgentApiKeyRotatedUtc { get; set; }

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

internal sealed class StoredPreviousAgentKey
{
    public string Hash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresUtc { get; set; }
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

    /// <summary>Son rapor, geçiş süresindeki önceki ajan anahtarıyla mı geldi.</summary>
    public bool UsesPreviousApiKey { get; set; }

    /// <summary>
    /// Son raporun güncel ürün tanımlarıyla hesaplanmış sonucu. Rapor veya ürün
    /// tanımları değişmedikçe pano her istekte yeniden hesaplamaz.
    /// </summary>
    [JsonIgnore]
    public MachineEvaluation? Evaluation { get; set; }
}

/// <summary>
/// Bir raporun belirli bir ürün tanımı listesine göre değerlendirme sonucu.
/// Kaynak nesnelerin kimliği saklanır; ikisinden biri değiştiğinde sonuç geçersizdir.
/// Listeler paylaşılır ve oluşturulduktan sonra değiştirilmez.
/// </summary>
/// <summary>Ajanın gönderdiği API anahtarının hangi kayıtla eşleştiği.</summary>
internal enum AgentKeyMatch
{
    None,
    Current,
    Previous
}

internal sealed record MachineEvaluation(
    AgentReportDto Report,
    List<ProductDefinitionDto> Products,
    List<ProductStatusDto> ProductStatuses,
    bool IsHealthy,
    List<string> RiskReasons);
