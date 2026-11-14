using BerTool.Shared.Contracts;
using BerTool.Shared.Products;
using BerTool.TrayAgent.Logging;
using BerTool.TrayAgent.Models;
using BerTool.TrayAgent.Reporting;

namespace BerTool.TrayAgent.Services;

internal sealed class TrayStatusEvaluator
{
    private readonly ServiceHealthChecker _serviceHealthChecker;
    private readonly BitLockerChecker _bitLockerChecker;
    private readonly SystemInfoProvider _systemInfoProvider;
    private readonly NetworkQualityChecker _networkQualityChecker;
    private readonly AgentConfigurationProvider _configurationProvider;

    public TrayStatusEvaluator(
        ServiceHealthChecker serviceHealthChecker,
        BitLockerChecker bitLockerChecker,
        SystemInfoProvider systemInfoProvider,
        NetworkQualityChecker networkQualityChecker,
        AgentConfigurationProvider configurationProvider)
    {
        _serviceHealthChecker = serviceHealthChecker;
        _bitLockerChecker = bitLockerChecker;
        _systemInfoProvider = systemInfoProvider;
        _networkQualityChecker = networkQualityChecker;
        _configurationProvider = configurationProvider;
    }

    public HealthStatus Evaluate()
    {
        try
        {
            var configuration = _configurationProvider.Current;
            var serviceResult = _serviceHealthChecker.GetServiceStatuses(configuration.Products);
            var serviceStatuses = serviceResult.Statuses;
            var systemInfo = _systemInfoProvider.GetSystemInfo();
            var networkQuality = _networkQualityChecker.GetNetworkQuality();
            var riskReasons = new List<string>();
            var isBitLockerApplicable = !IsVmwareModel(systemInfo.ComputerModel);
            var bitLockerVolumes = Array.Empty<BitLockerVolumeInfo>();

            if (serviceStatuses.Any(item => !item.IsHealthy))
            {
                riskReasons.Add("Servis kontrollerinden en az biri başarısız.");
            }

            if (isBitLockerApplicable)
            {
                bitLockerVolumes = _bitLockerChecker.GetVolumeStatuses().ToArray();

                // Sunucuyla aynı kural ve metinler: yöntem, koruma (askıya alınmış
                // mı) ve dönüştürme (şifreleme tamamlandı mı) birlikte değerlendirilir.
                riskReasons.AddRange(BitLockerRules.EvaluateRiskReasons(
                    bitLockerVolumes.Select(volume => new BitLockerVolumeDto
                    {
                        DriveLetter = volume.DriveLetter,
                        EncryptionMethod = volume.EncryptionMethod,
                        ProtectionOn = volume.ProtectionOn,
                        ConversionStatus = volume.ConversionStatus
                    }).ToList()));
            }

            return new HealthStatus
            {
                ServiceStatuses = serviceStatuses,
                ServiceSnapshots = serviceResult.Services,
                BitLockerVolumes = bitLockerVolumes,
                SystemInfo = systemInfo,
                NetworkQuality = networkQuality,
                RiskReasons = riskReasons,
                IsBitLockerApplicable = isBitLockerApplicable,
                IsHealthy = riskReasons.Count == 0,
                CheckedAt = DateTime.Now
            };
        }
        catch (Exception exception)
        {
            Logger.Error("Genel durum hesaplanamadı.", exception);
            return HealthStatus.CreateFallback("Genel durum hesaplanamadı.");
        }
    }

    private static bool IsVmwareModel(string computerModel)
    {
        return computerModel.Contains("VMWARE", StringComparison.OrdinalIgnoreCase);
    }
}
