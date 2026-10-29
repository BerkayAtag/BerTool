using BerTool.Shared.Contracts;
using BerTool.TrayAgent.Models;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BerTool.TrayAgent.Reporting;

internal static class AgentReportBuilder
{
    private static readonly Lazy<string> AgentVersion = new(ResolveAgentVersion, isThreadSafe: true);

    public static AgentReportDto Build(HealthStatus status, AgentConfigurationDto configuration)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(configuration);

        var systemInfo = status.SystemInfo;
        var network = status.NetworkQuality;

        return new AgentReportDto
        {
            AgentVersion = AgentVersion.Value,
            MachineId = MachineIdentityProvider.GetMachineId(),
            MachineName = systemInfo.MachineName,
            DomainOrWorkgroup = systemInfo.DomainOrWorkgroup,
            LoggedInUser = systemInfo.LoggedInUser,
            OperatingSystem = RuntimeInformation.OSDescription,
            ComputerModel = systemInfo.ComputerModel,
            CpuModel = systemInfo.CpuModel,
            PhysicalCoreCount = systemInfo.PhysicalCoreCount,
            LogicalProcessorCount = systemInfo.LogicalProcessorCount,
            TotalRamGb = Math.Round(systemInfo.TotalRamGb, 2),
            TotalDiskGb = Math.Round(systemInfo.TotalDiskGb, 2),
            PhysicalDiskDetails = systemInfo.PhysicalDiskDetails.ToList(),
            IpAddresses = systemInfo.IpAddresses.Select(item => new IpAddressDto
            {
                Address = item.Address,
                AdapterName = item.AdapterName,
                AdapterType = item.AdapterType,
                IsIpv6 = item.IsIpv6
            }).ToList(),
            Services = status.ServiceSnapshots.ToList(),
            ServiceStatuses = status.ServiceStatuses.Select(item => new ProductStatusDto
            {
                ProductId = item.ProductId,
                DisplayName = item.DisplayName,
                IsHealthy = item.IsHealthy,
                StatusText = item.StatusText,
                Details = item.Details
            }).ToList(),
            BitLockerVolumes = status.BitLockerVolumes.Select(item => new BitLockerVolumeDto
            {
                DriveLetter = item.DriveLetter,
                EncryptionMethod = item.EncryptionMethod,
                IsHealthy = item.IsHealthy
            }).ToList(),
            IsBitLockerApplicable = status.IsBitLockerApplicable,
            NetworkQuality = new NetworkQualityDto
            {
                HasActiveAdapter = network.HasActiveAdapter,
                HasGateway = network.HasGateway,
                AdapterName = network.AdapterName,
                BandwidthMbps = network.BandwidthMbps,
                InternetSpeedMbps = network.InternetSpeedMbps,
                InternetSpeedTarget = network.InternetSpeedTarget,
                LatencyMilliseconds = network.LatencyMilliseconds,
                LatencyTarget = network.LatencyTarget,
                CheckedAtUtc = new DateTimeOffset(network.CheckedAt)
            },
            RiskReasons = status.RiskReasons.ToList(),
            IsHealthy = status.IsHealthy,
            ConfigVersion = configuration.ConfigVersion,
            CheckedAtUtc = new DateTimeOffset(status.CheckedAt),
            CollectedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static string ResolveAgentVersion()
    {
        var assembly = typeof(AgentReportBuilder).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+')[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? "1.4.0";
    }
}
