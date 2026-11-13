using System.Net.NetworkInformation;

namespace BerTool.TrayAgent.Services;

/// <summary>
/// Ağ adaptörlerini türüne göre sınıflandırır ve IP adreslerini, makinenin
/// asıl ağ adresi en başta olacak şekilde sıralamak için öncelik üretir.
/// </summary>
internal static class NetworkAdapterClassifier
{
    public const string Physical = "Fiziksel";
    public const string Virtual = "Sanal";
    public const string Vpn = "VPN/Tünel";
    public const string Other = "Diğer";

    private static readonly string[] VpnKeywords =
    {
        "vpn",
        "tunnel",
        "wireguard",
        "openvpn",
        "anyconnect",
        "forti",
        "globalprotect",
        "zscaler",
        "pulse",
        "tap-",
        "tap adapter",
        "ppp"
    };

    // Hyper-V, VMware, VirtualBox, WSL ve Docker'ın ana makinede oluşturduğu
    // sanal anahtar ve adaptörler. Bunlar makinenin ağdaki asıl adresini taşımaz.
    // Sanal makine (VDI) içindeki asıl ağ kartı ("Microsoft Hyper-V Network
    // Adapter", "vmxnet3 Ethernet Adapter") bilinçli olarak bu listede değildir.
    private static readonly string[] VirtualKeywords =
    {
        "vethernet",
        "hyper-v virtual ethernet",
        "vmware virtual ethernet",
        "vmware network adapter vmnet",
        "vmnet",
        "virtualbox host-only",
        "host-only",
        "wsl",
        "docker",
        "loopback"
    };

    public static string ResolveAdapterType(NetworkInterfaceType interfaceType, string? name, string? description)
    {
        var metadata = $"{name} {description}".ToLowerInvariant();

        // VPN istemcilerinin çoğu adaptör adında/açıklamasında benzer anahtar kelimeler taşır.
        if (interfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp ||
            VpnKeywords.Any(keyword => metadata.Contains(keyword, StringComparison.Ordinal)))
        {
            return Vpn;
        }

        if (VirtualKeywords.Any(keyword => metadata.Contains(keyword, StringComparison.Ordinal)))
        {
            return Virtual;
        }

        if (interfaceType is NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.Wireless80211)
        {
            return Physical;
        }

        return Other;
    }

    /// <summary>
    /// Küçük değer listede daha önce gelir. Varsayılan ağ geçidi olan adaptör
    /// makinenin ağdaki asıl adresini taşır; konsoldaki makine listesinde ilk
    /// adres gösterildiği için en başa alınır.
    /// </summary>
    public static int GetSortPriority(string adapterType, bool hasGateway, bool isIpv6, bool isLinkLocal)
    {
        var priority = hasGateway ? 0 : 1_000;

        priority += adapterType switch
        {
            Physical => 0,
            Vpn => 100,
            Other => 200,
            _ => 300
        };

        if (isIpv6)
        {
            priority += 10;
        }

        if (isLinkLocal)
        {
            // fe80::/10 ve 169.254.0.0/16 adresleri yalnızca yerel bağlantıda anlamlıdır.
            priority += 20;
        }

        return priority;
    }
}
