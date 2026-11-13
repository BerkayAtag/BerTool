using BerTool.TrayAgent.Logging;

namespace BerTool.TrayAgent;

/// <summary>
/// Ajan zaten çalışırken ikinci kez başlatıldığında (ör. Başlat menüsündeki
/// kısayoldan) çalışan örneğe durum penceresini açmasını bildirir. Aksi halde
/// ikinci başlatma sessizce kapanır ve kullanıcı hiçbir şey görmez.
/// </summary>
internal static class SingleInstanceSignal
{
    // "Local\" öneki olayı oturuma özel yapar; her kullanıcı kendi ajanını açar.
    private const string ShowEventName = @"Local\BerTool.TrayAgent.ShowStatus";

    public static EventWaitHandle CreateListener()
    {
        return new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowEventName);
    }

    public static bool RequestShow()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ShowEventName, out var handle))
            {
                return false;
            }

            using (handle)
            {
                return handle.Set();
            }
        }
        catch (Exception exception)
        {
            Logger.Warn($"Çalışan BerTool örneğine pencere açma isteği iletilemedi: {exception.Message}");
            return false;
        }
    }
}
