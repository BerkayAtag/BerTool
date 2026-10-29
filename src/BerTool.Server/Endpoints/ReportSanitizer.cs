using BerTool.Shared.Contracts;
using System.Net;
using System.Text;

namespace BerTool.Server.Endpoints;

internal static class ReportSanitizer
{
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxReportAge = TimeSpan.FromDays(30);

    public static AgentReportDto Normalize(AgentReportDto report)
    {
        ArgumentNullException.ThrowIfNull(report);

        report.MachineId = (report.MachineId ?? string.Empty).Trim().ToLowerInvariant();
        report.MachineName = Required(report.MachineName, 128, "Makine adı");
        report.DomainOrWorkgroup = Text(report.DomainOrWorkgroup, 256);
        report.LoggedInUser = Text(report.LoggedInUser, 256);
        report.OperatingSystem = Text(report.OperatingSystem, 256);
        report.ComputerModel = Text(report.ComputerModel, 256);
        report.CpuModel = Text(report.CpuModel, 256);
        report.AgentVersion = Text(report.AgentVersion, 32);

        report.PhysicalCoreCount = Math.Clamp(report.PhysicalCoreCount, 0, 4096);
        report.LogicalProcessorCount = Math.Clamp(report.LogicalProcessorCount, 0, 65_536);
        report.TotalRamGb = NormalizeDouble(report.TotalRamGb);
        report.TotalDiskGb = NormalizeDouble(report.TotalDiskGb);

        report.PhysicalDiskDetails = (report.PhysicalDiskDetails ?? new List<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(64)
            .Select(item => Text(item, 512))
            .ToList();

        report.IpAddresses = (report.IpAddresses ?? new List<IpAddressDto>())
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Address))
            .Take(64)
            .Select(item => new IpAddressDto
            {
                Address = NormalizeAddress(item.Address),
                AdapterName = Text(item.AdapterName, 256),
                AdapterType = Text(item.AdapterType, 64),
                IsIpv6 = item.IsIpv6
            })
            .Where(item => item.Address.Length > 0)
            .ToList();

        report.Services = (report.Services ?? new List<ServiceSnapshotDto>())
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Name))
            .Take(1_500)
            .Select(item => new ServiceSnapshotDto
            {
                Name = Text(item.Name, 256),
                DisplayName = Text(item.DisplayName, 256),
                Status = Text(item.Status, 32)
            })
            .ToList();

        report.ServiceStatuses = new List<ProductStatusDto>();
        report.BitLockerVolumes = (report.BitLockerVolumes ?? new List<BitLockerVolumeDto>())
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.DriveLetter))
            .Take(64)
            .Select(item => new BitLockerVolumeDto
            {
                DriveLetter = NormalizeDriveLetter(item.DriveLetter),
                EncryptionMethod = Text(item.EncryptionMethod, 128),
                // Sağlık bayrağı ajandan alınmaz; tek kabul edilen yöntemden hesaplanır.
                IsHealthy = string.Equals(
                    Text(item.EncryptionMethod, 128),
                    "XTS-AES-256",
                    StringComparison.OrdinalIgnoreCase)
            })
            .ToList();

        report.NetworkQuality ??= new NetworkQualityDto();
        report.NetworkQuality.AdapterName = Text(report.NetworkQuality.AdapterName, 256);
        report.NetworkQuality.InternetSpeedTarget = Text(report.NetworkQuality.InternetSpeedTarget, 256);
        report.NetworkQuality.LatencyTarget = Text(report.NetworkQuality.LatencyTarget, 256);
        report.NetworkQuality.BandwidthMbps = NormalizeNullableDouble(report.NetworkQuality.BandwidthMbps);
        report.NetworkQuality.InternetSpeedMbps = NormalizeNullableDouble(report.NetworkQuality.InternetSpeedMbps);
        report.NetworkQuality.LatencyMilliseconds = NormalizeNullableLong(report.NetworkQuality.LatencyMilliseconds);

        report.RiskReasons = (report.RiskReasons ?? new List<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(50)
            .Select(item => Text(item, 500))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var collected = report.CollectedAtUtc;
        if (collected == default || collected > now + MaxFutureSkew || collected < now - MaxReportAge)
        {
            collected = now;
        }

        var checkedAt = report.CheckedAtUtc;
        if (checkedAt == default || checkedAt > now + MaxFutureSkew || checkedAt < now - MaxReportAge)
        {
            checkedAt = collected;
        }

        report.CollectedAtUtc = collected;
        report.CheckedAtUtc = checkedAt;

        return report;
    }

    private static string Required(string? value, int maxLength, string fieldName)
    {
        var trimmed = Text(value, maxLength);
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException($"{fieldName} boş olamaz.");
        }

        return trimmed;
    }

    /// <summary>
    /// Metin alanlarından kontrol ve yönlendirme karakterlerini temizler.
    /// Log enjeksiyonu, terminal kaçış dizileri ve görsel sahtecilik
    /// girişimlerinin sunucuya ulaşmasını engeller.
    /// </summary>
    private static string Text(string? value, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(trimmed.Length, maxLength));
        foreach (var character in trimmed)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            if (char.IsControl(character) || IsBidirectionalControl(character))
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    private static bool IsBidirectionalControl(char character)
    {
        return character is '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E'
            or '\u2066' or '\u2067' or '\u2068' or '\u2069';
    }

    private static string NormalizeAddress(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 64)
        {
            return string.Empty;
        }

        // IPv6 bölge kimliği (fe80::1%12) arayüz adı içerebilir; sınırla.
        if (IPAddress.TryParse(trimmed, out var address))
        {
            return address.ToString();
        }

        return string.Empty;
    }

    private static string NormalizeDriveLetter(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().ToUpperInvariant();
        return trimmed.Length == 1 && trimmed[0] is >= 'A' and <= 'Z'
            ? trimmed
            : Text(trimmed, 8);
    }

    private static double NormalizeDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            return 0;
        }

        return Math.Round(value, 2);
    }

    private static double? NormalizeNullableDouble(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < 0)
        {
            return null;
        }

        return Math.Round(value.Value, 2);
    }

    private static long? NormalizeNullableLong(long? value)
    {
        return value is < 0 ? null : value;
    }
}
