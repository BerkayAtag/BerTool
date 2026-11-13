using System.Globalization;
using System.Text;

namespace BerTool.Server.Storage;

/// <summary>
/// Arama karşılaştırması için metni sadeleştirir. Büyük/küçük harf, Türkçe
/// I/İ/ı/i ayrımı ve aksan işaretleri (ş, ğ, ü, ö, ç) yok sayılır; böylece
/// "istanbul" araması "İSTANBUL" kaydını, "antivirus" araması "Antivirüs"
/// ürününü bulur.
/// </summary>
internal static class SearchText
{
    public static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string decomposed;
        try
        {
            decomposed = value.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            // Geçersiz Unicode dizileri normalleştirilemez; olduğu gibi karşılaştırılır.
            decomposed = value;
        }

        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(character switch
            {
                'I' or 'ı' or 'İ' => 'i',
                _ => char.ToLowerInvariant(character)
            });
        }

        return builder.ToString();
    }

    /// <param name="value">Aranacak alan değeri.</param>
    /// <param name="foldedTerm"><see cref="Fold"/> ile sadeleştirilmiş arama terimi.</param>
    public static bool Contains(string? value, string foldedTerm)
    {
        return !string.IsNullOrEmpty(value) &&
            Fold(value).Contains(foldedTerm, StringComparison.Ordinal);
    }
}
