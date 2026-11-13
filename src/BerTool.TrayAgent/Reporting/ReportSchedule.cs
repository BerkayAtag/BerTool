namespace BerTool.TrayAgent.Reporting;

/// <summary>
/// Bir sonraki sunucu raporuna kadar beklenecek süreyi hesaplar. Rapor
/// zamanlayıcısı 45 saniyelik durum yenilemesinden bağımsızdır; böylece
/// konsolda ayarlanan rapor aralığı (ör. 30 veya 60 sn) birebir uygulanır.
/// </summary>
internal static class ReportSchedule
{
    /// <summary>Zamanlayıcının en kısa bekleme süresi; meşgul döngüyü engeller.</summary>
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Zamanlayıcının en uzun bekleme süresi. Daha ileri tarihli raporlar için
    /// zamanlayıcı bu sürede bir uyanıp yeniden planlar; saat değişikliği ve
    /// uyku sonrası sapmalar böylece kendiliğinden düzelir.
    /// </summary>
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(15);

    public static TimeSpan GetDelay(DateTimeOffset nextReportAtUtc, DateTimeOffset nowUtc)
    {
        var delay = nextReportAtUtc - nowUtc;
        if (delay < MinimumDelay)
        {
            return MinimumDelay;
        }

        return delay > MaximumDelay ? MaximumDelay : delay;
    }
}
