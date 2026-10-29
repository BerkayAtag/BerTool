using BerTool.Shared.Contracts;
using BerTool.Shared.Products;
using BerTool.TrayAgent.Logging;
using System.Text;
using System.Text.Json;

namespace BerTool.TrayAgent.Reporting;

internal sealed class AgentConfigurationProvider
{
    // Konsoldan yapılan ayar değişikliklerinin ajana makul sürede ulaşması
    // için sık aralıkla kontrol edilir. Değer, 45 saniyelik tray yenileme
    // zamanlayıcısından biraz kısa tutulur; böylece her yenilemede istek
    // atlanmaz ve değişiklik en geç 45 saniye içinde alınır. Değişiklik
    // yoksa yalnızca küçük bir yapılandırma yanıtı indirilir.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AuthenticationRetryInterval = TimeSpan.FromMinutes(10);

    private readonly object _sync = new();
    private readonly AgentApiClient _apiClient;
    private readonly AgentSettings _settings;
    private readonly string _cacheFilePath;

    private AgentConfigurationDto _current;
    private string _signature;
    private DateTimeOffset _nextFetchAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastSuccessAtUtc;
    private string? _lastError;
    private bool _fetchInProgress;

    public AgentConfigurationProvider(AgentApiClient apiClient, AgentSettings settings)
    {
        _apiClient = apiClient;
        _settings = settings;
        _cacheFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BerTool",
            "agent-config-cache.json");

        _current = CreateDefaultConfiguration();
        _signature = CreateSignature(_current);
        TryLoadCache();
    }

    public AgentConfigurationDto Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public DateTimeOffset? LastSuccessAtUtc
    {
        get
        {
            lock (_sync)
            {
                return _lastSuccessAtUtc;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_sync)
            {
                return _lastError;
            }
        }
    }

    public async Task<bool> EnsureFreshAsync(bool force, CancellationToken cancellationToken)
    {
        if (!_settings.IsConfigured)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (!force && now < _nextFetchAtUtc)
            {
                return false;
            }

            if (_fetchInProgress)
            {
                return false;
            }

            _fetchInProgress = true;
        }

        try
        {
            var fetched = await _apiClient.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            var normalized = Normalize(fetched);
            var changed = false;
            var newSignature = CreateSignature(normalized);

            lock (_sync)
            {
                changed = !string.Equals(newSignature, _signature, StringComparison.Ordinal);
                if (changed)
                {
                    _current = normalized;
                    _signature = newSignature;
                }

                _nextFetchAtUtc = DateTimeOffset.UtcNow + RefreshInterval;
                _lastSuccessAtUtc = DateTimeOffset.UtcNow;
                _lastError = null;
            }

            if (changed)
            {
                Logger.Info(
                    $"Sunucu ürün yapılandırması güncellendi. Sürüm: {normalized.ConfigVersion}, " +
                    $"ürün sayısı: {normalized.Products.Count}, rapor aralığı: {normalized.ReportIntervalSeconds} sn");
                SaveCache(normalized);
            }

            return changed;
        }
        catch (AgentAuthenticationException exception)
        {
            lock (_sync)
            {
                _nextFetchAtUtc = DateTimeOffset.UtcNow + AuthenticationRetryInterval;
                _lastError = exception.Message;
            }

            Logger.Error("Ajan API anahtarı sunucu tarafından reddedildi.", exception);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _nextFetchAtUtc = DateTimeOffset.UtcNow + FailureRetryInterval;
                _lastError = exception.Message;
            }

            Logger.Warn($"Sunucu yapılandırması alınamadı, mevcut tanımlar kullanılacak: {exception.Message}");
            return false;
        }
        finally
        {
            lock (_sync)
            {
                _fetchInProgress = false;
            }
        }
    }

    public void NotifyServerConfigVersion(int serverConfigVersion)
    {
        lock (_sync)
        {
            if (serverConfigVersion != _current.ConfigVersion)
            {
                // Sunucu daha yeni bir tanım sürümü bildirdi; bir sonraki durum
                // yenilemesinden önce yapılandırmayı hemen çekmeyi dene.
                _nextFetchAtUtc = DateTimeOffset.MinValue;
            }
        }
    }

    private AgentConfigurationDto CreateDefaultConfiguration()
    {
        var defaults = DefaultProductCatalog.CreateConfiguration(
            _settings.ReportIntervalSeconds,
            DefaultProductCatalog.DefaultOfflineThresholdMinutes);

        // Sürüm 0, yerel varsayılanların kullanıldığını ve sunucudan henüz
        // yapılandırma alınmadığını gösterir.
        defaults.ConfigVersion = 0;
        return defaults;
    }

    private static AgentConfigurationDto Normalize(AgentConfigurationDto configuration)
    {
        var products = new List<ProductDefinitionDto>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var product in configuration.Products ?? new List<ProductDefinitionDto>())
        {
            if (product is null)
            {
                continue;
            }

            var id = (product.Id ?? string.Empty).Trim();
            var displayName = (product.DisplayName ?? string.Empty).Trim();
            if (id.Length == 0 || displayName.Length == 0 || !seenIds.Add(id))
            {
                continue;
            }

            var serviceNames = (product.ServiceNames ?? new List<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();

            if (serviceNames.Count == 0)
            {
                continue;
            }

            products.Add(new ProductDefinitionDto
            {
                Id = id,
                DisplayName = displayName,
                ServiceNames = serviceNames,
                MatchMode = product.MatchMode == ServiceMatchMode.Any ? ServiceMatchMode.Any : ServiceMatchMode.All,
                Enabled = product.Enabled,
                SortOrder = product.SortOrder,
                UpdatedAtUtc = product.UpdatedAtUtc
            });
        }

        return new AgentConfigurationDto
        {
            ConfigVersion = Math.Max(0, configuration.ConfigVersion),
            ReportIntervalSeconds = Math.Clamp(configuration.ReportIntervalSeconds, 30, 86_400),
            OfflineThresholdMinutes = Math.Clamp(configuration.OfflineThresholdMinutes, 1, 1_440),
            Products = products
                .OrderBy(product => product.SortOrder)
                .ThenBy(product => product.DisplayName, StringComparer.CurrentCulture)
                .ToList()
        };
    }

    private void TryLoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath) || !_settings.IsConfigured)
            {
                return;
            }

            var json = File.ReadAllText(_cacheFilePath);
            var cache = JsonSerializer.Deserialize<AgentConfigurationCache>(json, ReportingJson.Options);
            if (cache?.Configuration is null ||
                !string.Equals(cache.ServerUrl, _settings.ServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var normalized = Normalize(cache.Configuration);
            lock (_sync)
            {
                _current = normalized;
                _signature = CreateSignature(normalized);
            }

            Logger.Info(
                $"Sunucu yapılandırması önbellekten yüklendi. Sürüm: {normalized.ConfigVersion}, " +
                $"önbellek zamanı: {cache.FetchedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}");
        }
        catch (Exception exception)
        {
            Logger.Warn($"Ajan yapılandırma önbelleği okunamadı: {exception.Message}");
        }
    }

    private void SaveCache(AgentConfigurationDto configuration)
    {
        var tempPath = $"{_cacheFilePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            var directory = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var cache = new AgentConfigurationCache
            {
                FetchedAtUtc = DateTimeOffset.UtcNow,
                ServerUrl = _settings.ServerUrl,
                Configuration = configuration
            };

            var json = JsonSerializer.Serialize(cache, ReportingJson.Options);
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, _cacheFilePath, overwrite: true);
        }
        catch (Exception exception)
        {
            Logger.Warn($"Ajan yapılandırma önbelleği yazılamadı: {exception.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Geçici dosya temizliği başarısız olsa da çalışma devam eder.
            }
        }
    }

    private static string CreateSignature(AgentConfigurationDto configuration)
    {
        var builder = new StringBuilder()
            .Append(configuration.ConfigVersion)
            .Append('|')
            .Append(configuration.ReportIntervalSeconds)
            .Append('|')
            .Append(configuration.OfflineThresholdMinutes);

        foreach (var product in configuration.Products)
        {
            builder
                .Append('|')
                .Append(product.Id)
                .Append(':')
                .Append(product.Enabled)
                .Append(':')
                .Append(product.MatchMode)
                .Append(':')
                .Append(product.SortOrder)
                .Append(':')
                .Append(string.Join(',', product.ServiceNames));
        }

        return builder.ToString();
    }

    private sealed class AgentConfigurationCache
    {
        public DateTimeOffset FetchedAtUtc { get; set; }
        public string ServerUrl { get; set; } = string.Empty;
        public AgentConfigurationDto Configuration { get; set; } = new();
    }
}
