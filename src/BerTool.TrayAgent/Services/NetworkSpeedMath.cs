namespace BerTool.TrayAgent.Services;

/// <summary>İndirme örneğinden internet hızını (Mbps) hesaplar.</summary>
internal static class NetworkSpeedMath
{
    /// <summary>
    /// Hız, ilk veri geldikten sonraki aktarım süresi üzerinden hesaplanır.
    /// Bağlantı kurulumu ve sunucunun ilk yanıt süresi (gecikme olarak ayrıca
    /// raporlanır) hıza dahil edilirse hızlı hatlarda sonuç olduğundan düşük çıkar.
    /// </summary>
    /// <param name="totalBytes">Okunan toplam bayt.</param>
    /// <param name="firstReadBytes">İlk okumada gelen bayt.</param>
    /// <param name="firstReadSeconds">İstek başlangıcından ilk okumanın bitişine kadar geçen süre.</param>
    /// <param name="totalSeconds">İstek başlangıcından son okumaya kadar geçen süre.</param>
    public static double CalculateMbps(long totalBytes, long firstReadBytes, double firstReadSeconds, double totalSeconds)
    {
        if (totalBytes <= 0 || totalSeconds <= 0)
        {
            return 0;
        }

        var transferBytes = totalBytes - Math.Max(0, firstReadBytes);
        var transferSeconds = totalSeconds - Math.Max(0, firstReadSeconds);

        // Örnek tek okumada geldiyse veya aktarım süresi ölçülemeyecek kadar
        // kısaysa toplam süre üzerinden hesaplanır.
        if (transferBytes < totalBytes / 2 || transferSeconds < 0.02)
        {
            return totalBytes * 8D / totalSeconds / 1_000_000D;
        }

        return transferBytes * 8D / transferSeconds / 1_000_000D;
    }
}
