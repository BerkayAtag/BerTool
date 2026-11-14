using BerTool.Shared.Contracts;

namespace BerTool.TrayAgent.Models;

internal sealed class HealthStatus
{
    public IReadOnlyList<ServiceStatusItem> ServiceStatuses { get; init; } = Array.Empty<ServiceStatusItem>();
    public IReadOnlyList<ServiceSnapshotDto> ServiceSnapshots { get; init; } = Array.Empty<ServiceSnapshotDto>();
    public IReadOnlyList<BitLockerVolumeInfo> BitLockerVolumes { get; init; } = Array.Empty<BitLockerVolumeInfo>();
    public SystemInfoModel SystemInfo { get; init; } = SystemInfoModel.CreateUnknown();
    public NetworkQualityInfo NetworkQuality { get; init; } = NetworkQualityInfo.CreateUnknown();
    public IReadOnlyList<string> RiskReasons { get; init; } = Array.Empty<string>();
    public bool IsBitLockerApplicable { get; init; } = true;
    public bool IsHealthy { get; init; }
    public DateTime CheckedAt { get; init; } = DateTime.Now;

    /// <summary>
    /// Gerçek ölçüm değil, hata/ilk yükleme yer tutucusu. Ekranda gösterilir
    /// ama sunucuya raporlanmaz: boş servis listesi sunucuda tüm ürünleri
    /// "Bulunamadı" gösteriyordu.
    /// </summary>
    public bool IsFallback { get; init; }

    public string DeviceStatus => IsFallback ? "Bilinmiyor" : IsHealthy ? "Sağlıklı" : "Riskli";
    public string TooltipText => IsFallback
        ? "BerTool: durum bilgisi alınamadı"
        : IsHealthy ? "Cihaz güvenli görünüyor" : "Güvende değilsiniz";

    public static HealthStatus CreateFallback(string reason)
    {
        return new HealthStatus
        {
            IsFallback = true,
            IsHealthy = false,
            RiskReasons = new[] { reason },
            SystemInfo = SystemInfoModel.CreateUnknown(),
            NetworkQuality = NetworkQualityInfo.CreateUnknown(),
            ServiceStatuses = Array.Empty<ServiceStatusItem>(),
            ServiceSnapshots = Array.Empty<ServiceSnapshotDto>(),
            BitLockerVolumes = Array.Empty<BitLockerVolumeInfo>(),
            CheckedAt = DateTime.Now
        };
    }
}
