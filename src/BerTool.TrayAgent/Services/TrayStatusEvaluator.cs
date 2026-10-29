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

                if (bitLockerVolumes.Length == 0)
                {
                    riskReasons.Add("BitLocker bilgisi okunamadı.");
                }
                else if (bitLockerVolumes.Any(volume => !volume.IsHealthy))
                {
                    riskReasons.Add("XTS-AES-256 dışındaki şifreleme tespit edildi.");
                }
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
