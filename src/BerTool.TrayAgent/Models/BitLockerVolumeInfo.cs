namespace BerTool.TrayAgent.Models;

internal sealed class BitLockerVolumeInfo
{
    public string DriveLetter { get; init; } = "N/A";
    public string EncryptionMethod { get; init; } = "Unknown";

    /// <summary>true: koruma açık, false: askıya alınmış/kapalı, null: okunamadı.</summary>
    public bool? ProtectionOn { get; init; }

    /// <summary>FullyEncrypted, EncryptionInProgress, ... Boş: okunamadı.</summary>
    public string ConversionStatus { get; init; } = string.Empty;

    public bool IsHealthy { get; init; }
    public string StatusIcon => IsHealthy ? "✔" : "✖";
}
