using System.Text;

namespace BerTool.TrayAgent.Logging;

internal static class Logger
{
    private const long MaxLogFileBytes = 2 * 1024 * 1024;

    private static readonly object SyncLock = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BerTool",
        "Logs");
    private static readonly string LogFilePath = Path.Combine(LogDirectory, "tray-agent.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            var builder = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [")
                .Append(level)
                .Append("] ")
                .AppendLine(Sanitize(message));

            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            lock (SyncLock)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateLogIfNeeded();
                File.AppendAllText(LogFilePath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception loggingException)
        {
            System.Diagnostics.Trace.WriteLine($"Log yazılamadı: {loggingException}");
        }
    }

    /// <summary>
    /// Log satırına yeni satır veya kontrol karakteri sızmasını engeller;
    /// böylece sahte zaman damgası ve log enjeksiyonu üretilemez.
    /// </summary>
    private static string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(message.Length, 4_000));
        foreach (var character in message)
        {
            if (builder.Length >= 4_000)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }

    private static void RotateLogIfNeeded()
    {
        var logFile = new FileInfo(LogFilePath);
        if (!logFile.Exists || logFile.Length < MaxLogFileBytes)
        {
            return;
        }

        var backupPath = LogFilePath + ".old";
        try
        {
            File.Move(LogFilePath, backupPath, overwrite: true);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"Log rotasyonu yapılamadı: {exception.Message}");
        }
    }
}
