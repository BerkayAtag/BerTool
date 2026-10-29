using BerTool.Server.Endpoints;
using BerTool.Shared.Contracts;
using Xunit;

namespace BerTool.Tests;

public sealed class ReportSanitizerTests
{
    [Fact]
    public void KontrolVeYonlendirmeKarakterleri_Temizlenir()
    {
        var report = CreateReport();
        report.MachineName = "PC\r\n2026-01-01 [ERROR] sahte log";
        report.ComputerModel = "Model\u202Egnp.exe";
        report.Services.Add(new ServiceSnapshotDto
        {
            Name = "BESClient\u0007",
            DisplayName = "BigFix\r\nAgent",
            Status = "Running"
        });

        var normalized = ReportSanitizer.Normalize(report);

        Assert.Equal("PC  2026-01-01 [ERROR] sahte log", normalized.MachineName);
        Assert.DoesNotContain('\u202E', normalized.ComputerModel);
        Assert.DoesNotContain('\u0007', normalized.Services[0].Name);
        Assert.DoesNotContain('\r', normalized.Services[0].DisplayName);
        Assert.DoesNotContain('\n', normalized.Services[0].DisplayName);
    }

    [Fact]
    public void GecersizIpAdresleri_Atilir()
    {
        var report = CreateReport();
        report.IpAddresses.Add(new IpAddressDto { Address = "192.168.1.10", AdapterName = "Ethernet" });
        report.IpAddresses.Add(new IpAddressDto { Address = "gecersiz-ip", AdapterName = "Kotu" });
        report.IpAddresses.Add(new IpAddressDto { Address = "  10.0.0.5  ", AdapterName = "Wi-Fi" });

        var normalized = ReportSanitizer.Normalize(report);

        Assert.Equal(2, normalized.IpAddresses.Count);
        Assert.Contains(normalized.IpAddresses, item => item.Address == "192.168.1.10");
        Assert.Contains(normalized.IpAddresses, item => item.Address == "10.0.0.5");
    }

    [Fact]
    public void GecersizTarihler_SimdikiZamanaCekilir()
    {
        var report = CreateReport();
        report.CollectedAtUtc = DateTimeOffset.UtcNow.AddYears(-1);
        report.CheckedAtUtc = DateTimeOffset.UtcNow.AddYears(1);

        var normalized = ReportSanitizer.Normalize(report);
        var now = DateTimeOffset.UtcNow;

        Assert.InRange(normalized.CollectedAtUtc, now.AddMinutes(-1), now.AddMinutes(1));
        Assert.Equal(normalized.CollectedAtUtc, normalized.CheckedAtUtc);
    }

    [Fact]
    public void GecerliRapor_Korunur()
    {
        var report = CreateReport();
        report.IpAddresses.Add(new IpAddressDto { Address = "fe80::1%12", AdapterName = "Ethernet" });
        report.BitLockerVolumes.Add(new BitLockerVolumeDto
        {
            DriveLetter = "c:",
            EncryptionMethod = "XTS-AES-256",
            IsHealthy = true
        });

        var normalized = ReportSanitizer.Normalize(report);

        Assert.Equal("TEST-PC", normalized.MachineName);
        Assert.Equal("fe80::1%12", normalized.IpAddresses[0].Address);
        Assert.Equal("C:", normalized.BitLockerVolumes[0].DriveLetter);
        Assert.Equal("XTS-AES-256", normalized.BitLockerVolumes[0].EncryptionMethod);
    }

    private static AgentReportDto CreateReport() => new()
    {
        MachineId = "0123456789ABCDEF0123456789ABCDEF",
        MachineName = "TEST-PC",
        DomainOrWorkgroup = "WORKGROUP",
        LoggedInUser = "test",
        OperatingSystem = "Windows 11",
        ComputerModel = "Test",
        CpuModel = "Test CPU",
        AgentVersion = "1.3.1",
        PhysicalCoreCount = 4,
        LogicalProcessorCount = 8,
        TotalRamGb = 16,
        TotalDiskGb = 512,
        Services = new List<ServiceSnapshotDto>(),
        IpAddresses = new List<IpAddressDto>(),
        BitLockerVolumes = new List<BitLockerVolumeDto>(),
        RiskReasons = new List<string>(),
        CollectedAtUtc = DateTimeOffset.UtcNow,
        CheckedAtUtc = DateTimeOffset.UtcNow
    };
}
