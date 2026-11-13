using BerTool.TrayAgent.Reporting;
using BerTool.TrayAgent.Services;
using System.Net.NetworkInformation;
using Xunit;

namespace BerTool.Tests;

public sealed class AgentLogicTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(300, 300)]
    public void RaporZamanlamasi_AyarlananAraligiBirebirUygular(int dueInSeconds, int expectedSeconds)
    {
        // 45 saniyelik durum yenilemesinden bağımsız: 30 sn 30 sn'dir, 45 sn değil.
        var delay = ReportSchedule.GetDelay(Now.AddSeconds(dueInSeconds), Now);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void RaporZamanlamasi_GecmisVeCokIleriZamanlariSinirlar()
    {
        // Süresi geçmiş (veya hiç gönderilmemiş) rapor hemen, ama meşgul döngü olmadan gönderilir.
        Assert.Equal(ReportSchedule.MinimumDelay, ReportSchedule.GetDelay(Now.AddMinutes(-5), Now));
        Assert.Equal(ReportSchedule.MinimumDelay, ReportSchedule.GetDelay(DateTimeOffset.MinValue, Now));

        // Çok ileri tarihli raporlarda zamanlayıcı ara ara uyanıp yeniden planlar.
        Assert.Equal(ReportSchedule.MaximumDelay, ReportSchedule.GetDelay(Now.AddHours(24), Now));
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Intel(R) Wi-Fi 6E AX211 160MHz", NetworkAdapterClassifier.Physical)]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet", "Realtek PCIe GbE Family Controller", NetworkAdapterClassifier.Physical)]
    [InlineData(NetworkInterfaceType.Ethernet, "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", NetworkAdapterClassifier.Virtual)]
    [InlineData(NetworkInterfaceType.Ethernet, "VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", NetworkAdapterClassifier.Virtual)]
    [InlineData(NetworkInterfaceType.Ethernet, "vEthernet (WSL (Hyper-V firewall))", "Hyper-V Virtual Ethernet Adapter #2", NetworkAdapterClassifier.Virtual)]
    [InlineData(NetworkInterfaceType.Ethernet, "VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter", NetworkAdapterClassifier.Virtual)]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet", "Microsoft Hyper-V Network Adapter", NetworkAdapterClassifier.Physical)]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet0", "vmxnet3 Ethernet Adapter", NetworkAdapterClassifier.Physical)]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet 3", "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter", NetworkAdapterClassifier.Vpn)]
    [InlineData(NetworkInterfaceType.Ethernet, "OpenVPN TAP-Windows6", "TAP-Windows Adapter V9", NetworkAdapterClassifier.Vpn)]
    [InlineData(NetworkInterfaceType.Tunnel, "Teredo", "Microsoft Teredo Tunneling Adapter", NetworkAdapterClassifier.Vpn)]
    [InlineData(NetworkInterfaceType.Ppp, "Kurum VPN", "WAN Miniport (IKEv2)", NetworkAdapterClassifier.Vpn)]
    public void AdaptorTuru_FizikselSanalVeVpnAyrimi(
        NetworkInterfaceType type,
        string name,
        string description,
        string expected)
    {
        Assert.Equal(expected, NetworkAdapterClassifier.ResolveAdapterType(type, name, description));
    }

    [Fact]
    public void AdaptorTuru_SiradanAdaptorlerFizikselSayilir()
    {
        // Anahtar kelimeler dar tutulur; "Adapter" veya "Dock" gibi sıradan
        // sözcükler bir adaptörü VPN ya da sanal olarak işaretlememelidir.
        Assert.Equal(
            NetworkAdapterClassifier.Physical,
            NetworkAdapterClassifier.ResolveAdapterType(
                NetworkInterfaceType.Ethernet,
                "Ethernet 2",
                "Laptop Dock USB Ethernet Adapter"));
        Assert.Equal(
            NetworkAdapterClassifier.Other,
            NetworkAdapterClassifier.ResolveAdapterType(
                NetworkInterfaceType.Unknown,
                "Bilinmeyen",
                "Generic Network Device"));
    }

    [Fact]
    public void IpSiralamasi_AgGecidiOlanFizikselAdresEnBastaGelir()
    {
        // Gerçek bir geliştirici makinesinden: Wi-Fi asıl bağlantı, diğerleri sanal anahtarlar.
        var wifi = NetworkAdapterClassifier.GetSortPriority(NetworkAdapterClassifier.Physical, hasGateway: true, isIpv6: false, isLinkLocal: false);
        var hyperV = NetworkAdapterClassifier.GetSortPriority(NetworkAdapterClassifier.Virtual, hasGateway: false, isIpv6: false, isLinkLocal: false);
        var vmware = NetworkAdapterClassifier.GetSortPriority(NetworkAdapterClassifier.Virtual, hasGateway: false, isIpv6: false, isLinkLocal: false);
        var wifiLinkLocalV6 = NetworkAdapterClassifier.GetSortPriority(NetworkAdapterClassifier.Physical, hasGateway: true, isIpv6: true, isLinkLocal: true);
        var vpnWithGateway = NetworkAdapterClassifier.GetSortPriority(NetworkAdapterClassifier.Vpn, hasGateway: true, isIpv6: false, isLinkLocal: false);
        var unpluggedEthernet = NetworkAdapterClassifier.GetSortPriority(NetworkAdapterClassifier.Physical, hasGateway: false, isIpv6: false, isLinkLocal: true);

        Assert.True(wifi < hyperV);
        Assert.True(wifi < vmware);
        Assert.True(wifi < wifiLinkLocalV6);
        Assert.True(wifi < vpnWithGateway);
        Assert.True(wifiLinkLocalV6 < hyperV);
        Assert.True(vpnWithGateway < hyperV);
        Assert.True(wifi < unpluggedEthernet);
    }

    [Fact]
    public void InternetHizi_IlkYanitSuresiHizaDahilEdilmez()
    {
        // 16 MB, 0,2 sn ilk yanıt + 0,256 sn aktarım: hat 500 Mbps'tir.
        const long total = 16L * 1024 * 1024;
        const long firstRead = 64 * 1024;

        var mbps = NetworkSpeedMath.CalculateMbps(total, firstRead, firstReadSeconds: 0.2, totalSeconds: 0.456);
        var naive = total * 8D / 0.456 / 1_000_000D;

        Assert.InRange(mbps, 480, 540);
        Assert.True(mbps > naive);
    }

    [Fact]
    public void InternetHizi_TekOkumadaVeyaGecersizGirdideGuvenliSonucVerir()
    {
        // Örnek tek okumada geldiyse toplam süre üzerinden hesaplanır.
        Assert.Equal(8, NetworkSpeedMath.CalculateMbps(1_000_000, 1_000_000, 1, 1), 3);

        Assert.Equal(0, NetworkSpeedMath.CalculateMbps(0, 0, 0, 1));
        Assert.Equal(0, NetworkSpeedMath.CalculateMbps(1_000, 0, 0, 0));
        Assert.True(double.IsFinite(NetworkSpeedMath.CalculateMbps(1_000_000, 10, 0.5, 0.5)));
    }
}
