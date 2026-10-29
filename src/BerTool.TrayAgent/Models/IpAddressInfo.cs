namespace BerTool.TrayAgent.Models;

internal sealed class IpAddressInfo
{
    public string Address { get; init; } = string.Empty;
    public string AdapterName { get; init; } = string.Empty;
    public string AdapterType { get; init; } = "Diğer";
    public bool IsIpv6 { get; init; }
}
