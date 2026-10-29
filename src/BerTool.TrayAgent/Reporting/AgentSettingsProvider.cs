using BerTool.TrayAgent.Logging;
using System.Text.Json;

namespace BerTool.TrayAgent.Reporting;

internal sealed class AgentSettings
{
    public bool Enabled { get; set; } = true;
    public string ServerUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int ReportIntervalSeconds { get; set; } = 300;

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(ServerUrl) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) &&
        IsAllowedTransport(uri);

    /// <summary>
    /// Uzak sunucular için yalnızca HTTPS kabul edilir. Yerel geliştirme ve
    /// tek makine kurulumlarında loopback üzerinden HTTP kullanılabilir.
    /// </summary>
    public static bool IsAllowedTransport(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }
}

internal sealed class AgentSettingsProvider
{
    private const string ServerUrlEnvironmentVariable = "BERTOOL_SERVER_URL";
    private const string ApiKeyEnvironmentVariable = "BERTOOL_AGENT_API_KEY";
    private const string EnabledEnvironmentVariable = "BERTOOL_REPORTING_ENABLED";
    private const string SettingsPathEnvironmentVariable = "BERTOOL_AGENT_SETTINGS_PATH";

    public AgentSettingsProvider()
    {
        SettingsFilePath = ResolveSettingsFilePath();
        Settings = LoadSettings(SettingsFilePath);
    }

    public string SettingsFilePath { get; }
    public AgentSettings Settings { get; }

    private static string ResolveSettingsFilePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(SettingsPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Environment.ExpandEnvironmentVariables(explicitPath.Trim());
        }

        var programDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "BerTool",
            "agent-settings.json");

        var localAppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BerTool",
            "agent-settings.json");

        // Makine genelindeki (ProgramData) kurulum ayarı önceliklidir. Böylece
        // yönetici tarafından yapılan merkezi yapılandırma, kullanıcının
        // LocalAppData altında kalan eski bir dosyasıyla ezilmez. Kullanıcıya
        // özel dosya yalnızca makine geneli dosya yoksa dikkate alınır.
        if (File.Exists(programDataPath))
        {
            return programDataPath;
        }

        return localAppDataPath;
    }

    private static AgentSettings LoadSettings(string filePath)
    {
        var settings = new AgentSettings();

        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                settings = JsonSerializer.Deserialize<AgentSettings>(json, ReportingJson.Options) ?? new AgentSettings();
            }
            catch (Exception exception)
            {
                Logger.Error($"Ajan ayar dosyası okunamadı: {filePath}", exception);
                settings = new AgentSettings();
            }
        }
        else
        {
            Logger.Info(
                $"Ajan sunucu ayarı bulunamadı. Beklenen dosya: {filePath}. " +
                "Sunucu raporlaması devre dışı kalacak.");
        }

        settings.ServerUrl = (settings.ServerUrl ?? string.Empty).Trim().TrimEnd('/');
        settings.ApiKey = (settings.ApiKey ?? string.Empty).Trim();
        settings.ReportIntervalSeconds = Math.Clamp(settings.ReportIntervalSeconds, 30, 86_400);

        var environmentUrl = Environment.GetEnvironmentVariable(ServerUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            settings.ServerUrl = environmentUrl.Trim().TrimEnd('/');
        }

        var environmentApiKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentApiKey))
        {
            settings.ApiKey = environmentApiKey.Trim();
        }

        var environmentEnabled = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable);
        if (bool.TryParse(environmentEnabled, out var enabled))
        {
            settings.Enabled = enabled;
        }

        if (!settings.Enabled)
        {
            Logger.Info("Sunucu raporlaması ayarlar üzerinden devre dışı bırakılmış.");
        }
        else if (Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var serverUri) &&
            serverUri.Scheme == Uri.UriSchemeHttp &&
            !serverUri.IsLoopback)
        {
            Logger.Error(
                "Güvenlik nedeniyle uzak sunucuya şifresiz HTTP ile raporlama yapılmaz. " +
                "agent-settings.json içindeki serverUrl değerini HTTPS olarak güncelleyin. " +
                $"Raporlama durduruldu: {settings.ServerUrl}");
        }
        else if (!settings.IsConfigured)
        {
            Logger.Warn(
                "Sunucu raporlaması yapılandırılmadı. agent-settings.json içinde geçerli " +
                "serverUrl ve apiKey değerleri bulunmalıdır.");
        }
        else
        {
            Logger.Info($"Sunucu raporlaması etkin: {settings.ServerUrl}");
        }

        return settings;
    }
}
