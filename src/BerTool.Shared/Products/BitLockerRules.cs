using BerTool.Shared.Contracts;

namespace BerTool.Shared.Products;

/// <summary>
/// BitLocker sağlık kuralları. Ajan ve sunucu aynı kuralları ve aynı risk
/// metinlerini kullanır; böylece sunucuda yeniden hesaplanan sonuç ajanın
/// gösterdiğiyle tutarlı kalır.
/// </summary>
public static class BitLockerRules
{
    public const string RequiredMethod = "XTS-AES-256";
    public const string FullyEncrypted = "FullyEncrypted";

    public const string UnreadableReason = "BitLocker bilgisi okunamadı.";
    public const string WeakMethodReason = "XTS-AES-256 dışındaki şifreleme tespit edildi.";
    public const string ProtectionOffReasonPrefix = "BitLocker koruması askıya alınmış veya kapalı:";
    public const string ConversionReasonPrefix = "BitLocker şifrelemesi tamamlanmamış:";

    public static bool IsRequiredMethod(string? encryptionMethod)
    {
        return string.Equals(encryptionMethod?.Trim(), RequiredMethod, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnknownMethod(string? encryptionMethod)
    {
        return string.IsNullOrWhiteSpace(encryptionMethod) ||
            string.Equals(encryptionMethod.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dönüştürme durumu bilinmiyorsa (eski ajan, okunamadı) ceza verilmez;
    /// yalnızca açıkça tamamlanmamış olduğu bildirilen birim risklidir.
    /// </summary>
    public static bool IsConversionComplete(string? conversionStatus)
    {
        return string.IsNullOrWhiteSpace(conversionStatus) ||
            string.Equals(conversionStatus.Trim(), FullyEncrypted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(conversionStatus.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Birim ancak yöntem XTS-AES-256, koruma açık (veya bilinmiyor) ve
    /// şifreleme tamamlanmış (veya bilinmiyor) ise sağlıklıdır. Eskiden yalnızca
    /// yönteme bakılıyordu: koruması askıya alınmış (Suspend-BitLocker) veya
    /// şifresi çözülmekte olan disk süresiz "Sağlıklı" görünüyordu.
    /// </summary>
    public static bool IsHealthy(string? encryptionMethod, bool? protectionOn, string? conversionStatus)
    {
        return IsRequiredMethod(encryptionMethod) &&
            protectionOn != false &&
            IsConversionComplete(conversionStatus);
    }

    public static bool IsHealthy(BitLockerVolumeDto volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return IsHealthy(volume.EncryptionMethod, volume.ProtectionOn, volume.ConversionStatus);
    }

    public static List<string> EvaluateRiskReasons(IReadOnlyCollection<BitLockerVolumeDto> volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        var reasons = new List<string>();

        if (volumes.Count == 0 || volumes.All(volume => IsUnknownMethod(volume.EncryptionMethod)))
        {
            reasons.Add(UnreadableReason);
            return reasons;
        }

        if (volumes.Any(volume =>
                !IsUnknownMethod(volume.EncryptionMethod) && !IsRequiredMethod(volume.EncryptionMethod)))
        {
            reasons.Add(WeakMethodReason);
        }

        if (volumes.Any(volume => IsUnknownMethod(volume.EncryptionMethod)))
        {
            reasons.Add(UnreadableReason);
        }

        var suspended = volumes
            .Where(volume => IsRequiredMethod(volume.EncryptionMethod) && volume.ProtectionOn == false)
            .Select(volume => volume.DriveLetter)
            .ToList();
        if (suspended.Count > 0)
        {
            reasons.Add($"{ProtectionOffReasonPrefix} {string.Join(", ", suspended)}");
        }

        var converting = volumes
            .Where(volume => IsRequiredMethod(volume.EncryptionMethod) && !IsConversionComplete(volume.ConversionStatus))
            .Select(volume => $"{volume.DriveLetter} ({DescribeConversion(volume.ConversionStatus)})")
            .ToList();
        if (converting.Count > 0)
        {
            reasons.Add($"{ConversionReasonPrefix} {string.Join(", ", converting)}");
        }

        return reasons;
    }

    /// <summary>Win32_EncryptableVolume ConversionStatus kodunu ada çevirir.</summary>
    public static string MapConversionStatus(uint code)
    {
        return code switch
        {
            0 => "FullyDecrypted",
            1 => FullyEncrypted,
            2 => "EncryptionInProgress",
            3 => "DecryptionInProgress",
            4 => "EncryptionPaused",
            5 => "DecryptionPaused",
            _ => "Unknown"
        };
    }

    public static string DescribeConversion(string? conversionStatus)
    {
        return (conversionStatus ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "FULLYENCRYPTED" => "tamamen şifreli",
            "FULLYDECRYPTED" => "şifresiz",
            "ENCRYPTIONINPROGRESS" => "şifreleniyor",
            "DECRYPTIONINPROGRESS" => "şifre çözülüyor",
            "ENCRYPTIONPAUSED" => "şifreleme duraklatıldı",
            "DECRYPTIONPAUSED" => "şifre çözme duraklatıldı",
            _ => "bilinmiyor"
        };
    }

    /// <summary>Ajanın kendi ürettiği BitLocker risk metni mi (sunucu yeniden hesaplar).</summary>
    public static bool IsBitLockerReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.Contains("BitLocker", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains(RequiredMethod, StringComparison.OrdinalIgnoreCase);
    }
}
