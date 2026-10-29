namespace BerTool.TrayAgent.Models;

internal sealed class SystemInfoModel
{
    public string MachineName { get; init; } = "Bilinmiyor";
    public string DomainOrWorkgroup { get; init; } = "Workgroup";
    public string LoggedInUser { get; init; } = "Bilinmiyor";
    public string ComputerModel { get; init; } = "Bilinmiyor";
    public string CpuModel { get; init; } = "Bilinmiyor";
    public int PhysicalCoreCount { get; init; }
    public int LogicalProcessorCount { get; init; }
    public double TotalRamGb { get; init; }
    public double TotalDiskGb { get; init; }
    public IReadOnlyList<string> PhysicalDiskDetails { get; init; } = Array.Empty<string>();
    public IReadOnlyList<IpAddressInfo> IpAddresses { get; init; } = Array.Empty<IpAddressInfo>();

    public static SystemInfoModel CreateUnknown()
    {
        return new SystemInfoModel
        {
            MachineName = Environment.MachineName,
            DomainOrWorkgroup = "Bilinmiyor",
            LoggedInUser = Environment.UserName,
            ComputerModel = "Bilinmiyor",
            CpuModel = "Bilinmiyor",
            PhysicalCoreCount = 0,
            LogicalProcessorCount = Environment.ProcessorCount,
            TotalRamGb = 0,
            TotalDiskGb = 0
        };
    }
}
