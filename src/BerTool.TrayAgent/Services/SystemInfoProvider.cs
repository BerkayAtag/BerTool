using BerTool.TrayAgent.Logging;
using BerTool.TrayAgent.Models;
using BerTool.TrayAgent.Utilities;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;

namespace BerTool.TrayAgent.Services;

internal sealed class SystemInfoProvider
{
    public SystemInfoModel GetSystemInfo()
    {
        var machineName = Environment.MachineName;
        var domainOrWorkgroup = "Workgroup";
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var loggedInUser = currentIdentity?.Name ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
        var computerModel = "Bilinmiyor";
        ulong totalPhysicalMemoryBytes = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Domain,Workgroup,PartOfDomain,Model,TotalPhysicalMemory,UserName FROM Win32_ComputerSystem");
            using var results = searcher.Get();
            using var systemInfo = results.Cast<ManagementObject>().FirstOrDefault();

            if (systemInfo is not null)
            {
                var partOfDomain = Convert.ToBoolean(systemInfo["PartOfDomain"] ?? false);
                domainOrWorkgroup = partOfDomain
                    ? (systemInfo["Domain"]?.ToString() ?? "Bilinmiyor")
                    : (systemInfo["Workgroup"]?.ToString() ?? "Workgroup");

                computerModel = systemInfo["Model"]?.ToString() ?? "Bilinmiyor";

                if (ulong.TryParse(systemInfo["TotalPhysicalMemory"]?.ToString(), out var parsedMemory))
                {
                    totalPhysicalMemoryBytes = parsedMemory;
                }

                var wmiUser = systemInfo["UserName"]?.ToString();
                if (!string.IsNullOrWhiteSpace(wmiUser))
                {
                    loggedInUser = wmiUser;
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Error("Win32_ComputerSystem bilgisi okunamadı.", exception);
        }

        var (cpuModel, physicalCoreCount, logicalProcessorCount) = GetCpuInfo();
        var (totalDiskBytes, diskDetails) = GetDiskInfo();
        var ipAddresses = GetIpAddresses();

        return new SystemInfoModel
        {
            MachineName = machineName,
            DomainOrWorkgroup = domainOrWorkgroup,
            LoggedInUser = loggedInUser,
            ComputerModel = computerModel,
            CpuModel = cpuModel,
            PhysicalCoreCount = physicalCoreCount,
            LogicalProcessorCount = logicalProcessorCount,
            TotalRamGb = SizeFormatter.BytesToGigabytes(totalPhysicalMemoryBytes),
            TotalDiskGb = SizeFormatter.BytesToGigabytes(totalDiskBytes),
            PhysicalDiskDetails = diskDetails,
            IpAddresses = ipAddresses
        };
    }

    private static (string CpuModel, int PhysicalCores, int LogicalProcessors) GetCpuInfo()
    {
        var cpuModel = "Bilinmiyor";
        var physicalCores = 0;
        var logicalProcessors = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name,NumberOfCores,NumberOfLogicalProcessors FROM Win32_Processor");
            using var results = searcher.Get();

            foreach (ManagementObject processor in results)
            {
                using (processor)
                {
                    cpuModel = processor["Name"]?.ToString() ?? cpuModel;
                    physicalCores += Convert.ToInt32(processor["NumberOfCores"] ?? 0);
                    logicalProcessors += Convert.ToInt32(processor["NumberOfLogicalProcessors"] ?? 0);
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Error("CPU bilgisi okunamadı.", exception);
        }

        if (logicalProcessors <= 0)
        {
            logicalProcessors = Environment.ProcessorCount;
        }

        if (physicalCores <= 0)
        {
            physicalCores = logicalProcessors;
        }

        return (cpuModel, physicalCores, logicalProcessors);
    }

    private static (ulong TotalDiskBytes, IReadOnlyList<string> DiskDetails) GetDiskInfo()
    {
        ulong totalDiskBytes = 0;
        var diskDetails = new List<string>();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Index,Model,Size FROM Win32_DiskDrive");
            using var results = searcher.Get();

            foreach (ManagementObject disk in results)
            {
                using (disk)
                {
                    if (!ulong.TryParse(disk["Size"]?.ToString(), out var sizeBytes) || sizeBytes == 0)
                    {
                        continue;
                    }

                    totalDiskBytes += sizeBytes;
                    var index = disk["Index"]?.ToString() ?? "?";
                    var model = disk["Model"]?.ToString() ?? "Bilinmiyor";
                    diskDetails.Add($"Disk {index}: {SizeFormatter.ToFriendlySize(sizeBytes)} ({model})");
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Error("Disk bilgisi okunamadı.", exception);
        }

        return (totalDiskBytes, diskDetails);
    }

    private static IReadOnlyList<IpAddressInfo> GetIpAddresses()
    {
        var addresses = new List<(IpAddressInfo Info, int Priority)>();

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var adapterType = NetworkAdapterClassifier.ResolveAdapterType(
                    networkInterface.NetworkInterfaceType,
                    networkInterface.Name,
                    networkInterface.Description);
                var ipProperties = networkInterface.GetIPProperties();
                var hasGateway = ipProperties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !gateway.Address.Equals(IPAddress.Any) &&
                    !gateway.Address.Equals(IPAddress.None));

                foreach (var unicastAddress in ipProperties.UnicastAddresses)
                {
                    var address = unicastAddress.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork &&
                        address.AddressFamily != AddressFamily.InterNetworkV6)
                    {
                        continue;
                    }

                    if (IPAddress.IsLoopback(address))
                    {
                        continue;
                    }

                    var isIpv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
                    var bytes = address.GetAddressBytes();
                    var isLinkLocal = isIpv6
                        ? address.IsIPv6LinkLocal
                        : bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;

                    addresses.Add((
                        new IpAddressInfo
                        {
                            Address = address.ToString(),
                            AdapterName = networkInterface.Name,
                            AdapterType = adapterType,
                            IsIpv6 = isIpv6
                        },
                        NetworkAdapterClassifier.GetSortPriority(adapterType, hasGateway, isIpv6, isLinkLocal)));
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Error("IP bilgileri alınamadı.", exception);
        }

        // Makinenin ağdaki asıl adresi (varsayılan ağ geçidi olan fiziksel
        // adaptör) en başta gelir. Konsoldaki makine listesi ilk adresi gösterir;
        // eskiden alfabetik sıralandığı için sanal anahtar adresleri öne çıkıyordu.
        return addresses
            .GroupBy(item => item.Info.Address, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.Priority).First())
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.Info.Address, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Info)
            .ToList();
    }
}
