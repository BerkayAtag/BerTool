using System.Collections.Concurrent;

namespace BerTool.Server.Storage;

/// <summary>
/// Makineler arasında tekrar eden kısa metinleri (servis adı, görünen ad,
/// durum) tek bir örnekte toplar. Her makine ~300 servis raporlar ve bu adlar
/// filo genelinde neredeyse aynıdır; havuz olmadan her rapor kendi kopyasını
/// bellekte tutar.
/// </summary>
internal static class StringPool
{
    private const int MaxEntries = 100_000;
    private const int MaxPooledLength = 160;

    private static readonly ConcurrentDictionary<string, string> Pool = new(StringComparer.Ordinal);

    public static string Intern(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Uzun metinler büyük olasılıkla benzersizdir; havuzu şişirmesin.
        if (value.Length > MaxPooledLength)
        {
            return value;
        }

        if (Pool.TryGetValue(value, out var existing))
        {
            return existing;
        }

        // Kötü niyetli bir ajanın benzersiz adlarla belleği doldurmasını engelle.
        return Pool.Count >= MaxEntries ? value : Pool.GetOrAdd(value, value);
    }
}
