namespace BerTool.Shared.Contracts;

/// <summary>
/// Bir ürünün beklenen servislerinin nasıl değerlendirileceğini belirtir.
/// </summary>
public enum ServiceMatchMode
{
    /// <summary>Tüm beklenen servisler bulunmalı ve çalışıyor olmalıdır.</summary>
    All = 0,

    /// <summary>Beklenen servislerden en az biri çalışıyor olmalıdır.</summary>
    Any = 1
}

/// <summary>
/// Sunucudan ajana gönderilen ürün tanımı.
/// </summary>
public sealed class ProductDefinitionDto
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> ServiceNames { get; set; } = new();
    public ServiceMatchMode MatchMode { get; set; } = ServiceMatchMode.All;
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// Ajanın sunucudan aldığı dinamik yapılandırma.
/// </summary>
public sealed class AgentConfigurationDto
{
    public int ConfigVersion { get; set; }
    public int ReportIntervalSeconds { get; set; } = 300;
    public int OfflineThresholdMinutes { get; set; } = 15;
    public List<ProductDefinitionDto> Products { get; set; } = new();
}

/// <summary>
/// Windows servis listesinin rapor için sadeleştirilmiş hali.
/// Sunucu, ürün tanımı değiştiğinde durumu bu anlık görüntüden yeniden hesaplar.
/// </summary>
public sealed class ServiceSnapshotDto
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Bir ürün için hesaplanan servis sağlığı sonucu.
/// </summary>
public sealed class ProductStatusDto
{
    public string ProductId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsHealthy { get; set; }
    public string StatusText { get; set; } = "Çalışmıyor";
    public string Details { get; set; } = string.Empty;
}

public sealed class BitLockerVolumeDto
{
    public string DriveLetter { get; set; } = string.Empty;
    public string EncryptionMethod { get; set; } = "N/A";
    public bool IsHealthy { get; set; }
}

public sealed class IpAddressDto
{
    public string Address { get; set; } = string.Empty;
    public string AdapterName { get; set; } = string.Empty;
    public string AdapterType { get; set; } = "Diğer";
    public bool IsIpv6 { get; set; }
}

public sealed class NetworkQualityDto
{
    public bool HasActiveAdapter { get; set; }
    public bool HasGateway { get; set; }
    public string AdapterName { get; set; } = "Bilinmiyor";
    public double? BandwidthMbps { get; set; }
    public double? InternetSpeedMbps { get; set; }
    public string InternetSpeedTarget { get; set; } = "download.windowsupdate.com";
    public long? LatencyMilliseconds { get; set; }
    public string LatencyTarget { get; set; } = "download.windowsupdate.com ilk byte";
    public DateTimeOffset CheckedAtUtc { get; set; }
}

/// <summary>
/// Ajanın sunucuya gönderdiği tam durum raporu.
/// </summary>
public sealed class AgentReportDto
{
    public string AgentVersion { get; set; } = "1.0.0";
    public string MachineId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string DomainOrWorkgroup { get; set; } = string.Empty;
    public string LoggedInUser { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string ComputerModel { get; set; } = string.Empty;
    public string CpuModel { get; set; } = string.Empty;
    public int PhysicalCoreCount { get; set; }
    public int LogicalProcessorCount { get; set; }
    public double TotalRamGb { get; set; }
    public double TotalDiskGb { get; set; }
    public List<string> PhysicalDiskDetails { get; set; } = new();
    public List<IpAddressDto> IpAddresses { get; set; } = new();
    public List<ServiceSnapshotDto> Services { get; set; } = new();
    public List<ProductStatusDto> ServiceStatuses { get; set; } = new();
    public List<BitLockerVolumeDto> BitLockerVolumes { get; set; } = new();
    public bool IsBitLockerApplicable { get; set; } = true;
    public NetworkQualityDto NetworkQuality { get; set; } = new();
    public List<string> RiskReasons { get; set; } = new();
    public bool IsHealthy { get; set; }
    public int ConfigVersion { get; set; }
    public DateTimeOffset CheckedAtUtc { get; set; }
    public DateTimeOffset CollectedAtUtc { get; set; }
}

public sealed class AgentReportAckDto
{
    public bool Accepted { get; set; }
    public int NextReportAfterSeconds { get; set; } = 300;
    public int ConfigVersion { get; set; }
    public DateTimeOffset ServerTimeUtc { get; set; }
}
