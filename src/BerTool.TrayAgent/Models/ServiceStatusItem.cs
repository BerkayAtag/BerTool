namespace BerTool.TrayAgent.Models;

internal sealed class ServiceStatusItem
{
    public string ProductId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = "Bilinmeyen Servis";
    public string StatusText { get; init; } = "Çalışmıyor";
    public bool IsHealthy { get; init; }
    public string Details { get; init; } = string.Empty;
    public string StatusIcon => IsHealthy ? "✔" : "✖";
}
