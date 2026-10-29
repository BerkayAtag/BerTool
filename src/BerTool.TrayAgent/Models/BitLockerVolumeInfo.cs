namespace BerTool.TrayAgent.Models;

internal sealed class BitLockerVolumeInfo
{
    public string DriveLetter { get; init; } = "N/A";
    public string EncryptionMethod { get; init; } = "N/A";
    public bool IsHealthy { get; init; }
    public string StatusIcon => IsHealthy ? "✔" : "✖";
}
