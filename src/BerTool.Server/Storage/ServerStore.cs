using BerTool.Server.Configuration;
using BerTool.Server.Security;
using BerTool.Shared.Contracts;
using BerTool.Shared.Products;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BerTool.Server.Storage;

internal sealed class ServerStore
{
    private const int MaxHistoryReplacementSeconds = 10;
    private const int MaxMachines = 10_000;
    private const int MaxStoredMachineFileBytes = 4 * 1024 * 1024;
    private static readonly Regex MachineIdPattern = new("^[a-fA-F0-9]{16,64}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<ServerStore> _logger;
    private readonly ServerOptions _options;
    private readonly IDataProtector _adPasswordProtector;
    private readonly string _dataDirectory;
    private readonly string _machinesDirectory;
    private readonly string _serverFilePath;
    private readonly Dictionary<string, StoredMachine> _machines = new(StringComparer.OrdinalIgnoreCase);

    private StoredServerData _data = new();
    private AdOptions _adOptions = new();
    private string? _adPasswordError;
    private bool _initialized;

    public ServerStore(ServerOptions options, string resolvedDataDirectory, ILogger<ServerStore> logger)
        : this(
            options,
            resolvedDataDirectory,
            logger,
            new EphemeralDataProtectionProvider())
    {
    }

    public ServerStore(
        ServerOptions options,
        string resolvedDataDirectory,
        ILogger<ServerStore> logger,
        IDataProtectionProvider dataProtectionProvider)
    {
        _options = options;
        _logger = logger;
        _dataDirectory = resolvedDataDirectory;
        _machinesDirectory = Path.Combine(_dataDirectory, "machines");
        _serverFilePath = Path.Combine(_dataDirectory, "server.json");
        _adPasswordProtector = dataProtectionProvider.CreateProtector("BerTool.Ad.BindPassword.v1");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_dataDirectory);
            Directory.CreateDirectory(_machinesDirectory);

            if (File.Exists(_serverFilePath))
            {
                var json = await File.ReadAllTextAsync(_serverFilePath, cancellationToken).ConfigureAwait(false);
                _data = JsonSerializer.Deserialize<StoredServerData>(json, JsonOptions)
                    ?? throw new InvalidOperationException("Sunucu yapılandırma dosyası okunamadı.");
                ValidateLoadedData(_data);
            }
            else
            {
                await CreateInitialDataAsync(cancellationToken).ConfigureAwait(false);
            }

            await InitializeAdSettingsAsync(cancellationToken).ConfigureAwait(false);
            await LoadMachinesAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;

            _logger.LogInformation(
                "BerTool sunucusu {MachineCount} makine kaydı ve {ProductCount} ürün tanımıyla hazır. Veri dizini: {DataDirectory}",
                _machines.Count,
                _data.Products.Count,
                _dataDirectory);
        }
        catch (Exception exception)
        {
            _logger.LogCritical(exception, "Sunucu veri deposu başlatılamadı.");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool ValidateAgentApiKey(string? apiKey)
    {
        return PasswordHasher.VerifyAgentApiKey(apiKey ?? string.Empty, _data.AgentApiKeyHash);
    }

    /// <summary>
    /// Geçerli AD ayarlarının kopyasını döndürür. Parola yalnızca sunucu
    /// belleğinde tutulur; HTTP yanıtlarına veya loglara yazılmaz.
    /// </summary>
    public AdOptions GetAdOptions()
    {
        return _adOptions.Clone();
    }

    public AdStatusDto GetAdStatus()
    {
        var status = _adOptions.ToStatus();
        if (!string.IsNullOrEmpty(_adPasswordError))
        {
            status.Configured = false;
            status.ConfigurationError = _adPasswordError;
        }

        return status;
    }

    /// <summary>
    /// Konsoldaki form değerlerinden kaydetmeden bir AD taslağı üretir.
    /// Boş parola alanı mevcut kayıtlı parolayı korur.
    /// </summary>
    public AdOptions CreateAdDraft(AdSettingsUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var draft = _adOptions.Clone();
        ApplyAdRequest(draft, request);
        return draft;
    }

    public async Task<AdOptions> UpdateAdSettingsAsync(
        AdSettingsUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Şifresiz LDAP yalnızca yapılandırma dosyasından açılabilir. Konsol
        // formu LDAPS veya StartTLS dışında bir seçenek sunmaz; API üzerinden
        // de şifrelemesiz kayıt yapılmasına izin verilmez.
        if (!request.UseSsl && !request.UseStartTls)
        {
            throw new StoreValidationException(
                "Konsoldan yalnızca LDAPS veya StartTLS seçilebilir. " +
                "Şifresiz LDAP yalnızca appsettings.json üzerinden yapılandırılabilir.");
        }

        var candidate = CreateAdDraft(request);
        var validationError = candidate.Validate();
        if (validationError is not null)
        {
            throw new StoreValidationException(validationError);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousStored = _data.Ad;
            var previousOptions = _adOptions;
            var previousPasswordError = _adPasswordError;

            _data.Ad = CreateStoredAdSettings(candidate);
            _data.UpdatedAtUtc = DateTimeOffset.UtcNow;

            try
            {
                await SaveServerDataAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _data.Ad = previousStored;
                _adOptions = previousOptions;
                _adPasswordError = previousPasswordError;
                throw;
            }

            _adOptions = candidate;
            _adPasswordError = null;

            _logger.LogInformation(
                "Active Directory ayarları güncellendi. Durum: {Enabled}, sunucu: {Host}:{Port}, şifreleme: {Encryption}, " +
                "yerel yedek giriş: {LocalFallback}",
                candidate.Enabled ? "etkin" : "kapalı",
                candidate.Host,
                candidate.EffectivePort,
                candidate.EncryptionName,
                candidate.AllowLocalFallback ? "açık" : "kapalı");

            return candidate.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AgentConfigurationDto> GetAgentConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new AgentConfigurationDto
            {
                ConfigVersion = _data.ConfigVersion,
                ReportIntervalSeconds = _data.ReportIntervalSeconds,
                OfflineThresholdMinutes = _data.OfflineThresholdMinutes,
                Products = _data.Products
                    .Where(product => product.Enabled)
                    .OrderBy(product => product.SortOrder)
                    .ThenBy(product => product.DisplayName, StringComparer.CurrentCulture)
                    .Select(CloneProduct)
                    .ToList()
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AgentReportAckDto> SaveReportAsync(
        AgentReportDto report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!MachineIdPattern.IsMatch(report.MachineId))
        {
            throw new StoreValidationException("Geçersiz makine kimliği.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var machineId = report.MachineId.ToLowerInvariant();

            report.MachineId = machineId;
            report.ServiceStatuses = ProductHealthEvaluator.EvaluateAll(_data.Products, report.Services);

            var (isHealthy, riskReasons) = EvaluateRisk(report, report.ServiceStatuses);
            report.IsHealthy = isHealthy;
            report.RiskReasons = riskReasons;

            if (report.CollectedAtUtc == default)
            {
                report.CollectedAtUtc = now;
            }

            if (report.CheckedAtUtc == default)
            {
                report.CheckedAtUtc = report.CollectedAtUtc;
            }

            if (!_machines.ContainsKey(machineId) && _machines.Count >= MaxMachines)
            {
                throw new StoreValidationException(
                    "Sunucu makine kayıt sınırına ulaştı. Yeni makine raporları kabul edilemiyor.");
            }

            _machines.TryGetValue(machineId, out var record);
            var isNewMachine = record is null;
            record ??= new StoredMachine
            {
                MachineId = machineId,
                FirstSeenUtc = now
            };

            record.LatestReport = report;
            record.LastSeenUtc = now;
            record.History ??= new List<MachineHistoryEntryDto>();
            record.History.RemoveAll(item => item is null);

            var historyEntry = new MachineHistoryEntryDto
            {
                CollectedAtUtc = report.CollectedAtUtc,
                ReceivedAtUtc = now,
                IsHealthy = isHealthy,
                RiskReasons = riskReasons.ToList(),
                ConfigVersion = report.ConfigVersion
            };

            if (record.History.Count > 0 &&
                now - record.History[^1].ReceivedAtUtc < TimeSpan.FromSeconds(MaxHistoryReplacementSeconds))
            {
                record.History[^1] = historyEntry;
            }
            else
            {
                record.History.Add(historyEntry);
            }

            if (record.History.Count > _data.HistoryLimit)
            {
                record.History.RemoveRange(0, record.History.Count - _data.HistoryLimit);
            }

            await WriteMachineAsync(record, cancellationToken).ConfigureAwait(false);
            _machines[machineId] = record;

            if (isNewMachine)
            {
                _logger.LogInformation(
                    "Yeni makine raporu alındı: {MachineName} ({MachineId})",
                    report.MachineName,
                    machineId);
            }

            return new AgentReportAckDto
            {
                Accepted = true,
                NextReportAfterSeconds = _data.ReportIntervalSeconds,
                ConfigVersion = _data.ConfigVersion,
                ServerTimeUtc = now
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DashboardResponseDto> GetDashboardAsync(
        string? status,
        string? search,
        string? productId,
        string? productState,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var summaries = _machines.Values
                .Select(record => BuildSummary(record, now))
                .ToList();

            var summary = new DashboardSummaryDto
            {
                Total = summaries.Count,
                Healthy = summaries.Count(item => item.Status == "healthy"),
                Risky = summaries.Count(item => item.Status == "risky"),
                Offline = summaries.Count(item => item.Status == "offline"),
                OutdatedConfiguration = summaries.Count(item => !item.IsConfigCurrent)
            };

            var productFilters = _data.Products
                .OrderBy(product => product.SortOrder)
                .ThenBy(product => product.DisplayName, StringComparer.CurrentCulture)
                .Select(product => new ProductFilterDto
                {
                    Id = product.Id,
                    DisplayName = product.DisplayName,
                    Enabled = product.Enabled,
                    UnhealthyMachineCount = summaries.Count(item =>
                        item.Status != "offline" &&
                        item.ProductStatuses.Any(productStatus =>
                            string.Equals(productStatus.ProductId, product.Id, StringComparison.OrdinalIgnoreCase) &&
                            !productStatus.IsHealthy))
                })
                .ToList();

            IEnumerable<MachineSummaryDto> query = summaries;

            if (!string.IsNullOrWhiteSpace(status) &&
                !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(item => string.Equals(item.Status, status, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(productId))
            {
                query = query.Where(item => item.ProductStatuses.Any(productStatus =>
                    string.Equals(productStatus.ProductId, productId, StringComparison.OrdinalIgnoreCase)));

                if (string.Equals(productState, "unhealthy", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(item => item.ProductStatuses.Any(productStatus =>
                        string.Equals(productStatus.ProductId, productId, StringComparison.OrdinalIgnoreCase) &&
                        !productStatus.IsHealthy));
                }
                else if (string.Equals(productState, "healthy", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(item => item.ProductStatuses.Any(productStatus =>
                        string.Equals(productStatus.ProductId, productId, StringComparison.OrdinalIgnoreCase) &&
                        productStatus.IsHealthy));
                }
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(item => MatchesSearch(item, term));
            }

            var ordered = query
                .OrderBy(item => item.Status switch
                {
                    "risky" => 0,
                    "healthy" => 1,
                    _ => 2
                })
                .ThenByDescending(item => item.LastSeenUtc)
                .ThenBy(item => item.MachineName, StringComparer.CurrentCulture)
                .ToList();

            var filteredCount = ordered.Count;
            page = Math.Clamp(page, 1, 1_000_000);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var pageItems = ordered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return new DashboardResponseDto
            {
                Summary = summary,
                FilteredCount = filteredCount,
                Machines = pageItems,
                Products = productFilters,
                GeneratedAtUtc = now
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MachineDetailDto?> GetMachineDetailAsync(
        string machineId,
        CancellationToken cancellationToken = default)
    {
        if (!MachineIdPattern.IsMatch(machineId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_machines.TryGetValue(machineId, out var record))
            {
                return null;
            }

            var summary = BuildSummary(record, DateTimeOffset.UtcNow);
            return new MachineDetailDto
            {
                Summary = summary,
                LatestReport = record.LatestReport,
                History = record.History
                    .OrderByDescending(item => item.ReceivedAtUtc)
                    .Take(_data.HistoryLimit)
                    .ToList()
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<ProductDefinitionDto>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _data.Products
                .OrderBy(product => product.SortOrder)
                .ThenBy(product => product.DisplayName, StringComparer.CurrentCulture)
                .Select(CloneProduct)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProductDefinitionDto> CreateProductAsync(
        ProductUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalized = NormalizeProductRequest(request);
            EnsureUniqueProductName(normalized.DisplayName, null);

            var product = new ProductDefinitionDto
            {
                Id = CreateUniqueProductId(normalized.DisplayName),
                DisplayName = normalized.DisplayName,
                ServiceNames = normalized.ServiceNames,
                MatchMode = normalized.MatchMode,
                Enabled = normalized.Enabled,
                SortOrder = normalized.SortOrder > 0
                    ? normalized.SortOrder
                    : (_data.Products.Count == 0 ? 10 : _data.Products.Max(item => item.SortOrder) + 10),
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            var previousProducts = _data.Products;
            var previousVersion = _data.ConfigVersion;
            _data.Products = previousProducts.Append(product).ToList();
            _data.ConfigVersion++;
            _data.UpdatedAtUtc = DateTimeOffset.UtcNow;

            try
            {
                await SaveServerDataAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _data.Products = previousProducts;
                _data.ConfigVersion = previousVersion;
                throw;
            }

            _logger.LogInformation("Ürün tanımı oluşturuldu: {ProductName} ({ProductId})", product.DisplayName, product.Id);
            return CloneProduct(product);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProductDefinitionDto> UpdateProductAsync(
        string productId,
        ProductUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalized = NormalizeProductRequest(request);
            var existing = _data.Products.FirstOrDefault(product =>
                string.Equals(product.Id, productId, StringComparison.OrdinalIgnoreCase))
                ?? throw new StoreNotFoundException("Ürün tanımı bulunamadı.");

            EnsureUniqueProductName(normalized.DisplayName, existing.Id);

            var previousProducts = _data.Products;
            var previousVersion = _data.ConfigVersion;
            var updated = CloneProduct(existing);
            updated.DisplayName = normalized.DisplayName;
            updated.ServiceNames = normalized.ServiceNames;
            updated.MatchMode = normalized.MatchMode;
            updated.Enabled = normalized.Enabled;
            updated.SortOrder = normalized.SortOrder;
            updated.UpdatedAtUtc = DateTimeOffset.UtcNow;

            _data.Products = previousProducts
                .Select(product => string.Equals(product.Id, existing.Id, StringComparison.OrdinalIgnoreCase) ? updated : product)
                .ToList();
            _data.ConfigVersion++;
            _data.UpdatedAtUtc = DateTimeOffset.UtcNow;

            try
            {
                await SaveServerDataAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _data.Products = previousProducts;
                _data.ConfigVersion = previousVersion;
                throw;
            }

            _logger.LogInformation("Ürün tanımı güncellendi: {ProductName} ({ProductId})", updated.DisplayName, updated.Id);
            return CloneProduct(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteProductAsync(string productId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _data.Products.FirstOrDefault(product =>
                string.Equals(product.Id, productId, StringComparison.OrdinalIgnoreCase))
                ?? throw new StoreNotFoundException("Ürün tanımı bulunamadı.");

            var previousProducts = _data.Products;
            var previousVersion = _data.ConfigVersion;
            _data.Products = previousProducts
                .Where(product => !string.Equals(product.Id, existing.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _data.ConfigVersion++;
            _data.UpdatedAtUtc = DateTimeOffset.UtcNow;

            try
            {
                await SaveServerDataAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _data.Products = previousProducts;
                _data.ConfigVersion = previousVersion;
                throw;
            }

            _logger.LogInformation("Ürün tanımı silindi: {ProductName} ({ProductId})", existing.DisplayName, existing.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdminSettingsDto> GetAdminSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new AdminSettingsDto
            {
                AdminUsername = _data.AdminUsername,
                ReportIntervalSeconds = _data.ReportIntervalSeconds,
                OfflineThresholdMinutes = _data.OfflineThresholdMinutes,
                HistoryLimit = _data.HistoryLimit,
                ConfigVersion = _data.ConfigVersion,
                AgentApiKeyConfigured = !string.IsNullOrWhiteSpace(_data.AgentApiKeyHash),
                Ad = GetAdStatus(),
                ServerTimeUtc = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdminSettingsDto> UpdateSettingsAsync(
        UpdateAdminSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ReportIntervalSeconds is < 30 or > 86_400)
        {
            throw new StoreValidationException("Rapor aralığı 30 ile 86400 saniye arasında olmalıdır.");
        }

        if (request.OfflineThresholdMinutes is < 1 or > 1_440)
        {
            throw new StoreValidationException("Çevrimdışı eşiği 1 ile 1440 dakika arasında olmalıdır.");
        }

        if (request.HistoryLimit is < 5 or > 500)
        {
            throw new StoreValidationException("Geçmiş kayıt sayısı 5 ile 500 arasında olmalıdır.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settingsChanged =
                request.ReportIntervalSeconds != _data.ReportIntervalSeconds ||
                request.OfflineThresholdMinutes != _data.OfflineThresholdMinutes ||
                request.HistoryLimit != _data.HistoryLimit;

            if (!settingsChanged)
            {
                return await GetAdminSettingsSnapshotUnsafeAsync().ConfigureAwait(false);
            }

            var previousInterval = _data.ReportIntervalSeconds;
            var previousOffline = _data.OfflineThresholdMinutes;
            var previousHistory = _data.HistoryLimit;
            var previousVersion = _data.ConfigVersion;

            _data.ReportIntervalSeconds = request.ReportIntervalSeconds;
            _data.OfflineThresholdMinutes = request.OfflineThresholdMinutes;
            _data.HistoryLimit = request.HistoryLimit;
            _data.ConfigVersion++;
            _data.UpdatedAtUtc = DateTimeOffset.UtcNow;

            try
            {
                await SaveServerDataAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _data.ReportIntervalSeconds = previousInterval;
                _data.OfflineThresholdMinutes = previousOffline;
                _data.HistoryLimit = previousHistory;
                _data.ConfigVersion = previousVersion;
                throw;
            }

            if (request.HistoryLimit < previousHistory)
            {
                await TrimMachineHistoriesAsync(cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Sunucu ayarları güncellendi. Rapor aralığı: {ReportInterval}s, çevrimdışı eşiği: {OfflineThreshold}dk, " +
                "geçmiş sınırı: {HistoryLimit}, yapılandırma sürümü: {ConfigVersion}",
                _data.ReportIntervalSeconds,
                _data.OfflineThresholdMinutes,
                _data.HistoryLimit,
                _data.ConfigVersion);

            return await GetAdminSettingsSnapshotUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var policyError = PasswordPolicy.Validate(newPassword);
        if (policyError is not null)
        {
            throw new StoreValidationException(policyError);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!PasswordHasher.Verify(currentPassword, _data.AdminPassword))
            {
                throw new StoreValidationException("Mevcut yönetici şifresi doğru değil.");
            }

            if (PasswordHasher.Verify(newPassword, _data.AdminPassword))
            {
                throw new StoreValidationException("Yeni şifre mevcut şifreyle aynı olamaz.");
            }

            var previousPassword = _data.AdminPassword;
            var previousSessionVersion = _data.SessionVersion;
            _data.AdminPassword = PasswordHasher.Hash(newPassword);
            _data.SessionVersion = Math.Max(1, _data.SessionVersion + 1);
            _data.UpdatedAtUtc = DateTimeOffset.UtcNow;

            try
            {
                await SaveServerDataAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _data.AdminPassword = previousPassword;
                _data.SessionVersion = previousSessionVersion;
                throw;
            }

            _logger.LogInformation("Yönetici şifresi değiştirildi.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> RotateAgentApiKeyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var apiKey = PasswordHasher.GenerateSecret(32);
            var previousHash = _data.AgentApiKeyHash;
            _data.AgentApiKeyHash = PasswordHasher.HashAgentApiKey(apiKey);
            _data.UpdatedAtUtc = DateTimeOffset.UtcNow;

            try
            {
                await SaveServerDataAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _data.AgentApiKeyHash = previousHash;
                throw;
            }

            _logger.LogWarning("Ajan API anahtarı yenilendi. Tüm ajanların ayarları güncellenmelidir.");
            return apiKey;
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool VerifyAdminCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        return string.Equals(username.Trim(), _data.AdminUsername, StringComparison.OrdinalIgnoreCase) &&
            PasswordHasher.Verify(password, _data.AdminPassword);
    }

    public int GetSessionVersion()
    {
        return Math.Max(1, _data.SessionVersion);
    }

    public async Task<List<MachineSummaryDto>> GetFilteredMachinesForExportAsync(
        string? status,
        string? search,
        string? productId,
        string? productState,
        CancellationToken cancellationToken = default)
    {
        var dashboard = await GetDashboardAsync(
            status,
            search,
            productId,
            productState,
            page: 1,
            pageSize: 100,
            cancellationToken).ConfigureAwait(false);

        if (dashboard.FilteredCount <= dashboard.Machines.Count)
        {
            return dashboard.Machines;
        }

        var all = new List<MachineSummaryDto>(dashboard.Machines);
        var page = 2;
        while (all.Count < dashboard.FilteredCount)
        {
            var next = await GetDashboardAsync(
                status,
                search,
                productId,
                productState,
                page,
                pageSize: 100,
                cancellationToken).ConfigureAwait(false);

            if (next.Machines.Count == 0)
            {
                break;
            }

            all.AddRange(next.Machines);
            page++;
        }

        return all;
    }

    private async Task TrimMachineHistoriesAsync(CancellationToken cancellationToken)
    {
        foreach (var record in _machines.Values)
        {
            record.History ??= new List<MachineHistoryEntryDto>();
            record.History.RemoveAll(item => item is null);

            if (record.History.Count <= _data.HistoryLimit)
            {
                continue;
            }

            record.History.RemoveRange(0, record.History.Count - _data.HistoryLimit);

            try
            {
                await WriteMachineAsync(record, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Makine geçmişi yeni sınıra göre kaydedilemedi: {MachineId}",
                    record.MachineId);
            }
        }
    }

    private async Task InitializeAdSettingsAsync(CancellationToken cancellationToken)
    {
        if (_data.Ad is null)
        {
            // Eski server.json dosyalarında AD bölümü yoktur. İlk geçişte
            // appsettings.json değerleri şifreli olarak veri deposuna taşınır.
            var seed = _options.Ad?.Clone() ?? new AdOptions();
            _data.Ad = CreateStoredAdSettings(seed);
            _data.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await SaveServerDataAsync(cancellationToken).ConfigureAwait(false);
            _adOptions = seed;
            _adPasswordError = null;

            _logger.LogInformation(
                "Active Directory ayarları veri deposuna taşındı. Durum: {Enabled}, sunucu: {Host}:{Port}",
                seed.Enabled ? "etkin" : "kapalı",
                seed.Host,
                seed.EffectivePort);
            return;
        }

        NormalizeStoredAdSettings(_data.Ad);
        _adOptions = CreateAdOptionsFromStored(_data.Ad, out var passwordError);
        _adPasswordError = passwordError;

        if (!string.IsNullOrEmpty(passwordError))
        {
            _logger.LogError(
                "Active Directory hizmet hesabı parolası çözülemedi. Konsoldan yeni parola girilmesi gerekiyor: {Error}",
                passwordError);
        }

        if (_adOptions.Enabled && _adOptions.Validate() is { } configurationError)
        {
            _logger.LogError(
                "Active Directory yapılandırması geçersiz olduğu için AD girişi kullanılamıyor: {Error}",
                configurationError);
        }
    }

    private StoredAdSettings CreateStoredAdSettings(AdOptions options)
    {
        return new StoredAdSettings
        {
            Enabled = options.Enabled,
            Host = options.Host?.Trim() ?? string.Empty,
            Port = Math.Clamp(options.Port, 0, 65_535),
            UseSsl = options.UseSsl,
            UseStartTls = options.UseStartTls,
            BaseDn = options.BaseDn?.Trim() ?? string.Empty,
            BindDn = options.BindDn?.Trim() ?? string.Empty,
            ProtectedBindPassword = _adPasswordProtector.Protect(options.BindPassword ?? string.Empty),
            AdminGroup = options.AdminGroup?.Trim() ?? string.Empty,
            AllowLocalFallback = options.AllowLocalFallback,
            TimeoutSeconds = Math.Clamp(options.TimeoutSeconds, 3, 60)
        };
    }

    private AdOptions CreateAdOptionsFromStored(StoredAdSettings stored, out string? passwordError)
    {
        passwordError = null;
        var password = string.Empty;

        if (!string.IsNullOrEmpty(stored.ProtectedBindPassword))
        {
            try
            {
                password = _adPasswordProtector.Unprotect(stored.ProtectedBindPassword);
            }
            catch (Exception exception)
            {
                passwordError =
                    "Kayıtlı AD hizmet hesabı parolası çözülemedi. Konsoldan hizmet hesabı parolasını yeniden girin.";
                _logger.LogError(exception, "AD hizmet hesabı parolası Data Protection ile çözülemedi.");
            }
        }

        return new AdOptions
        {
            Enabled = stored.Enabled,
            Host = stored.Host ?? string.Empty,
            Port = stored.Port,
            UseSsl = stored.UseSsl,
            UseStartTls = stored.UseStartTls,

            // Güvenlik açısından riskli iki ayar yalnızca yapılandırma
            // dosyasından okunur; veri deposundaki eski değerler yok sayılır.
            IgnoreCertificateErrors = _options.Ad?.IgnoreCertificateErrors == true,
            AllowInsecureLdap = _options.Ad?.AllowInsecureLdap == true,

            BaseDn = stored.BaseDn ?? string.Empty,
            BindDn = stored.BindDn ?? string.Empty,
            BindPassword = password,
            AdminGroup = stored.AdminGroup ?? string.Empty,
            AllowLocalFallback = stored.AllowLocalFallback,
            TimeoutSeconds = stored.TimeoutSeconds
        };
    }

    private static void ApplyAdRequest(AdOptions target, AdSettingsUpdateRequest request)
    {
        target.Enabled = request.Enabled;
        target.Host = (request.Host ?? string.Empty).Trim();
        target.Port = request.Port;
        target.UseSsl = request.UseSsl;
        target.UseStartTls = request.UseStartTls;
        target.BaseDn = (request.BaseDn ?? string.Empty).Trim();
        target.BindDn = (request.BindDn ?? string.Empty).Trim();

        // Boş parola, mevcut kayıtlı parolanın korunması anlamına gelir.
        if (!string.IsNullOrEmpty(request.BindPassword))
        {
            target.BindPassword = request.BindPassword;
        }

        target.AdminGroup = (request.AdminGroup ?? string.Empty).Trim();
        target.AllowLocalFallback = request.AllowLocalFallback;
        target.TimeoutSeconds = request.TimeoutSeconds;

        // Sertifika doğrulamasını devre dışı bırakma ve şifresiz LDAP izni
        // yalnızca appsettings.json üzerinden yönetilir; konsol bu değerleri
        // değiştiremez.
    }

    private static void NormalizeStoredAdSettings(StoredAdSettings stored)
    {
        stored.Host = Truncate(stored.Host?.Trim() ?? string.Empty, 255);
        stored.BaseDn = Truncate(stored.BaseDn?.Trim() ?? string.Empty, 1024);
        stored.BindDn = Truncate(stored.BindDn?.Trim() ?? string.Empty, 1024);
        stored.AdminGroup = Truncate(stored.AdminGroup?.Trim() ?? string.Empty, 1024);
        stored.Port = Math.Clamp(stored.Port, 0, 65_535);
        stored.TimeoutSeconds = Math.Clamp(stored.TimeoutSeconds, 3, 60);

        if (stored.ProtectedBindPassword?.Length > 16_384)
        {
            stored.ProtectedBindPassword = string.Empty;
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private async Task CreateInitialDataAsync(CancellationToken cancellationToken)
    {
        var generatedItems = new List<(string Label, string Value)>();
        var adminUsername = string.IsNullOrWhiteSpace(_options.AdminUsername)
            ? "admin"
            : _options.AdminUsername.Trim();

        string adminPassword;
        if (string.IsNullOrWhiteSpace(_options.AdminPassword))
        {
            adminPassword = PasswordHasher.GenerateSecret(18);
            generatedItems.Add(("Admin şifre", adminPassword));
        }
        else
        {
            adminPassword = _options.AdminPassword;
        }

        string agentApiKey;
        if (string.IsNullOrWhiteSpace(_options.AgentApiKey))
        {
            agentApiKey = PasswordHasher.GenerateSecret(32);
            generatedItems.Add(("Ajan API anahtarı", agentApiKey));
        }
        else
        {
            agentApiKey = _options.AgentApiKey;
        }

        // Varsayılan ürünler ilk kurulum anında oluşturulmuş sayılır; konsoldaki
        // "Güncellendi" sütunu aksi halde boş görünür.
        var createdAtUtc = DateTimeOffset.UtcNow;
        var defaultProducts = DefaultProductCatalog.Create().ToList();
        foreach (var product in defaultProducts)
        {
            product.UpdatedAtUtc = createdAtUtc;
        }

        _data = new StoredServerData
        {
            AdminUsername = adminUsername,
            AdminPassword = PasswordHasher.Hash(adminPassword),
            AgentApiKeyHash = PasswordHasher.HashAgentApiKey(agentApiKey),
            ReportIntervalSeconds = DefaultProductCatalog.DefaultReportIntervalSeconds,
            OfflineThresholdMinutes = DefaultProductCatalog.DefaultOfflineThresholdMinutes,
            HistoryLimit = DefaultProductCatalog.DefaultHistoryLimit,
            ConfigVersion = DefaultProductCatalog.DefaultConfigVersion,
            Products = defaultProducts,
            UpdatedAtUtc = createdAtUtc
        };

        await SaveServerDataAsync(cancellationToken).ConfigureAwait(false);

        if (generatedItems.Count > 0)
        {
            var bootstrapPath = Path.Combine(_dataDirectory, "bootstrap-credentials.txt");
            var builder = new StringBuilder()
                .AppendLine("BerTool Sunucu - İlk Kurulum Bilgileri")
                .AppendLine($"Oluşturulma: {DateTimeOffset.Now:dd.MM.yyyy HH:mm:ss}")
                .AppendLine($"Admin kullanıcı: {adminUsername}");

            foreach (var item in generatedItems)
            {
                builder.AppendLine($"{item.Label}: {item.Value}");
            }

            builder.AppendLine()
                .AppendLine("Bu dosyayı güvenli bir yere kopyalayıp sunucudan silin.");

            await File.WriteAllTextAsync(
                bootstrapPath,
                builder.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            _logger.LogWarning(
                "İlk kurulum kimlik bilgileri oluşturuldu. Dosya: {BootstrapPath}. İçeriği güvenli bir yere taşıyıp dosyayı silin.",
                bootstrapPath);
        }

        _logger.LogInformation("Varsayılan ürün tanımları ve sunucu ayarları oluşturuldu.");
    }

    private void ValidateLoadedData(StoredServerData data)
    {
        if (string.IsNullOrWhiteSpace(data.AdminUsername) || data.AdminPassword is null)
        {
            throw new InvalidOperationException("Sunucu yapılandırmasında yönetici bilgileri eksik.");
        }

        if (string.IsNullOrWhiteSpace(data.AgentApiKeyHash))
        {
            throw new InvalidOperationException("Sunucu yapılandırmasında ajan API anahtarı eksik.");
        }

        data.AdminUsername = data.AdminUsername.Trim();
        if (data.AdminUsername.Length > 256)
        {
            data.AdminUsername = data.AdminUsername[..256];
        }

        data.Products = (data.Products ?? new List<ProductDefinitionDto>())
            .Where(product => product is not null)
            .ToList();
        data.ReportIntervalSeconds = Math.Clamp(data.ReportIntervalSeconds, 30, 86_400);
        data.OfflineThresholdMinutes = Math.Clamp(data.OfflineThresholdMinutes, 1, 1_440);
        data.HistoryLimit = Math.Clamp(data.HistoryLimit, 5, 500);
        data.ConfigVersion = Math.Max(1, data.ConfigVersion);
        data.SessionVersion = Math.Max(1, data.SessionVersion);

        foreach (var product in data.Products)
        {
            product.ServiceNames = (product.ServiceNames ?? new List<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();
            product.DisplayName = product.DisplayName?.Trim() ?? string.Empty;
            product.Id = product.Id?.Trim() ?? string.Empty;
        }

        data.Products.RemoveAll(product =>
            string.IsNullOrWhiteSpace(product.Id) ||
            string.IsNullOrWhiteSpace(product.DisplayName));

        var seenProductIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        data.Products.RemoveAll(product => !seenProductIds.Add(product.Id));
    }

    private async Task LoadMachinesAsync(CancellationToken cancellationToken)
    {
        _machines.Clear();

        foreach (var filePath in Directory.EnumerateFiles(_machinesDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > MaxStoredMachineFileBytes)
                {
                    _logger.LogWarning("Boyut sınırını aşan makine kaydı atlandı: {FilePath}", filePath);
                    continue;
                }

                var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                var record = JsonSerializer.Deserialize<StoredMachine>(json, JsonOptions);
                if (record is null ||
                    string.IsNullOrWhiteSpace(record.MachineId) ||
                    !MachineIdPattern.IsMatch(record.MachineId) ||
                    record.LatestReport is null)
                {
                    _logger.LogWarning("Geçersiz makine kaydı atlandı: {FilePath}", filePath);
                    continue;
                }

                record.MachineId = record.MachineId.ToLowerInvariant();
                NormalizeStoredReport(record.LatestReport);
                if (record.LastSeenUtc == default)
                {
                    record.LastSeenUtc = record.LatestReport.CollectedAtUtc;
                }

                if (record.FirstSeenUtc == default)
                {
                    record.FirstSeenUtc = record.LastSeenUtc;
                }

                record.History = (record.History ?? new List<MachineHistoryEntryDto>())
                    .Where(item => item is not null)
                    .OrderBy(item => item.ReceivedAtUtc)
                    .ToList();

                if (record.History.Count > _data.HistoryLimit)
                {
                    record.History.RemoveRange(0, record.History.Count - _data.HistoryLimit);
                }

                _machines[record.MachineId] = record;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Makine kaydı okunamadı, atlandı: {FilePath}", filePath);
            }
        }
    }

    private async Task WriteMachineAsync(StoredMachine record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_machinesDirectory);
        var filePath = Path.Combine(_machinesDirectory, $"{record.MachineId.ToLowerInvariant()}.json");
        await WriteJsonAtomicAsync(filePath, record, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveServerDataAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataDirectory);
        await WriteJsonAtomicAsync(_serverFilePath, _data, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAtomicAsync<T>(
        string filePath,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(value, JsonOptions);
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            File.Move(tempPath, filePath, overwrite: true);
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
                // Geçici dosya temizliği başarısız olsa da asıl işlem sonucu değişmez.
            }
        }
    }

    private static void NormalizeStoredReport(AgentReportDto report)
    {
        report.MachineId = (report.MachineId ?? string.Empty).Trim().ToLowerInvariant();
        report.MachineName = string.IsNullOrWhiteSpace(report.MachineName) ? "Bilinmiyor" : report.MachineName.Trim();
        report.DomainOrWorkgroup = report.DomainOrWorkgroup?.Trim() ?? string.Empty;
        report.LoggedInUser = report.LoggedInUser?.Trim() ?? string.Empty;
        report.OperatingSystem = report.OperatingSystem?.Trim() ?? string.Empty;
        report.ComputerModel = report.ComputerModel?.Trim() ?? string.Empty;
        report.CpuModel = report.CpuModel?.Trim() ?? string.Empty;
        report.AgentVersion = report.AgentVersion?.Trim() ?? string.Empty;
        report.PhysicalDiskDetails = (report.PhysicalDiskDetails ?? new List<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        report.IpAddresses = (report.IpAddresses ?? new List<IpAddressDto>())
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Address))
            .ToList();
        report.Services = (report.Services ?? new List<ServiceSnapshotDto>())
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Name))
            .ToList();
        report.ServiceStatuses = new List<ProductStatusDto>();
        report.BitLockerVolumes = (report.BitLockerVolumes ?? new List<BitLockerVolumeDto>())
            .Where(item => item is not null)
            .ToList();
        report.RiskReasons = (report.RiskReasons ?? new List<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        report.NetworkQuality ??= new NetworkQualityDto();
    }

    private MachineSummaryDto BuildSummary(StoredMachine record, DateTimeOffset now)
    {
        var report = record.LatestReport;
        var services = report.Services ?? new List<ServiceSnapshotDto>();
        var ipAddresses = report.IpAddresses ?? new List<IpAddressDto>();
        var productStatuses = ProductHealthEvaluator.EvaluateAll(_data.Products, services);
        var (isHealthy, riskReasons) = EvaluateRisk(report, productStatuses);
        var lastSeen = record.LastSeenUtc == default ? report.CollectedAtUtc : record.LastSeenUtc;
        var firstSeen = record.FirstSeenUtc == default ? lastSeen : record.FirstSeenUtc;
        var isOffline = now - lastSeen > TimeSpan.FromMinutes(_data.OfflineThresholdMinutes);

        return new MachineSummaryDto
        {
            MachineId = record.MachineId,
            MachineName = string.IsNullOrWhiteSpace(report.MachineName) ? "Bilinmiyor" : report.MachineName,
            DomainOrWorkgroup = report.DomainOrWorkgroup ?? string.Empty,
            LoggedInUser = report.LoggedInUser ?? string.Empty,
            OperatingSystem = report.OperatingSystem ?? string.Empty,
            ComputerModel = report.ComputerModel ?? string.Empty,
            IpAddresses = ipAddresses
                .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Address))
                .Select(item => item.Address)
                .ToList(),
            Status = isOffline ? "offline" : isHealthy ? "healthy" : "risky",
            StatusText = isOffline ? "Çevrimdışı" : isHealthy ? "Sağlıklı" : "Riskli",
            IsHealthy = isHealthy,
            IsOffline = isOffline,
            RiskReasons = riskReasons,
            ProductStatuses = productStatuses,
            FirstSeenUtc = firstSeen,
            LastSeenUtc = lastSeen,
            CollectedAtUtc = report.CollectedAtUtc,
            ConfigVersion = report.ConfigVersion,
            IsConfigCurrent = report.ConfigVersion == _data.ConfigVersion,
            AgentVersion = report.AgentVersion ?? string.Empty
        };
    }

    private static (bool IsHealthy, List<string> RiskReasons) EvaluateRisk(
        AgentReportDto report,
        IReadOnlyCollection<ProductStatusDto> productStatuses)
    {
        var reasons = new List<string>();
        var failingProducts = productStatuses.Where(status => !status.IsHealthy).ToList();

        if (failingProducts.Count > 0)
        {
            reasons.Add($"Servis sorunu: {string.Join(", ", failingProducts.Select(status => status.DisplayName))}");
        }

        // BitLocker riski ajanın kendi risk listesine bırakılmaz; raporlanan
        // birimlerden sunucuda yeniden hesaplanır. Metinler ajanla aynıdır,
        // böylece aşağıdaki birleştirmede tekrar oluşmaz.
        if (report.IsBitLockerApplicable)
        {
            var volumes = report.BitLockerVolumes ?? new List<BitLockerVolumeDto>();
            if (volumes.Count == 0)
            {
                reasons.Add("BitLocker bilgisi okunamadı.");
            }
            else if (volumes.Any(volume => !string.Equals(
                         volume.EncryptionMethod?.Trim(),
                         "XTS-AES-256",
                         StringComparison.OrdinalIgnoreCase)))
            {
                reasons.Add("XTS-AES-256 dışındaki şifreleme tespit edildi.");
            }
        }

        foreach (var reason in report.RiskReasons ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                continue;
            }

            var trimmed = reason.Trim();

            // Ajanın eski ürün tanımına göre ürettiği genel servis özeti yerine
            // sunucunun güncel tanımlarla hesapladığı sonuç kullanılır.
            if (trimmed.StartsWith("Servis kontrollerinden", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Servis sorunu:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!reasons.Contains(trimmed, StringComparer.CurrentCultureIgnoreCase))
            {
                reasons.Add(trimmed);
            }
        }

        return (reasons.Count == 0, reasons);
    }

    private static bool MatchesSearch(MachineSummaryDto item, string term)
    {
        return Contains(item.MachineName, term) ||
            Contains(item.DomainOrWorkgroup, term) ||
            Contains(item.LoggedInUser, term) ||
            Contains(item.OperatingSystem, term) ||
            Contains(item.ComputerModel, term) ||
            item.IpAddresses.Any(address => Contains(address, term)) ||
            item.RiskReasons.Any(reason => Contains(reason, term)) ||
            item.ProductStatuses.Any(status =>
                Contains(status.DisplayName, term) || Contains(status.Details, term));
    }

    private static bool Contains(string? value, string term)
    {
        return !string.IsNullOrEmpty(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static ProductUpsertRequest NormalizeProductRequest(ProductUpsertRequest request)
    {
        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length == 0)
        {
            throw new StoreValidationException("Ürün adı boş olamaz.");
        }

        if (displayName.Length > 100)
        {
            throw new StoreValidationException("Ürün adı en fazla 100 karakter olabilir.");
        }

        var services = (request.ServiceNames ?? new List<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (services.Count == 0)
        {
            throw new StoreValidationException("En az bir servis adı girilmelidir.");
        }

        if (services.Count > 20)
        {
            throw new StoreValidationException("Bir ürün için en fazla 20 servis adı tanımlanabilir.");
        }

        if (services.Any(name => name.Length > 256))
        {
            throw new StoreValidationException("Servis adları en fazla 256 karakter olabilir.");
        }

        if (request.MatchMode is not (ServiceMatchMode.All or ServiceMatchMode.Any))
        {
            throw new StoreValidationException("Geçersiz servis eşleşme modu.");
        }

        return new ProductUpsertRequest
        {
            DisplayName = displayName,
            ServiceNames = services,
            MatchMode = request.MatchMode,
            Enabled = request.Enabled,
            SortOrder = request.SortOrder
        };
    }

    private void EnsureUniqueProductName(string displayName, string? excludedProductId)
    {
        var exists = _data.Products.Any(product =>
            !string.Equals(product.Id, excludedProductId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(product.DisplayName, displayName, StringComparison.CurrentCultureIgnoreCase));

        if (exists)
        {
            throw new StoreValidationException("Bu ada sahip bir ürün zaten var.");
        }
    }

    private string CreateUniqueProductId(string displayName)
    {
        var slug = CreateSlug(displayName);
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "urun";
        }

        if (slug.Length > 48)
        {
            slug = slug[..48].Trim('-');
        }

        var candidate = slug;
        var suffix = 2;
        while (_data.Products.Any(product => string.Equals(product.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{slug}-{suffix++}";
        }

        return candidate;
    }

    private static string CreateSlug(string value)
    {
        var normalized = value
            .Replace('ı', 'i')
            .Replace('İ', 'I')
            .Replace('ş', 's')
            .Replace('Ş', 'S')
            .Replace('ğ', 'g')
            .Replace('Ğ', 'G')
            .Replace('ü', 'u')
            .Replace('Ü', 'U')
            .Replace('ö', 'o')
            .Replace('Ö', 'O')
            .Replace('ç', 'c')
            .Replace('Ç', 'C')
            .Normalize(NormalizationForm.FormD);

        var builder = new StringBuilder();
        foreach (var character in normalized)
        {
            if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (character is ' ' or '-' or '_')
            {
                if (builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }
            }
        }

        return builder.ToString().Trim('-');
    }

    private static ProductDefinitionDto CloneProduct(ProductDefinitionDto product)
    {
        return new ProductDefinitionDto
        {
            Id = product.Id,
            DisplayName = product.DisplayName,
            ServiceNames = product.ServiceNames.ToList(),
            MatchMode = product.MatchMode,
            Enabled = product.Enabled,
            SortOrder = product.SortOrder,
            UpdatedAtUtc = product.UpdatedAtUtc
        };
    }

    private Task<AdminSettingsDto> GetAdminSettingsSnapshotUnsafeAsync()
    {
        return Task.FromResult(new AdminSettingsDto
        {
            AdminUsername = _data.AdminUsername,
            ReportIntervalSeconds = _data.ReportIntervalSeconds,
            OfflineThresholdMinutes = _data.OfflineThresholdMinutes,
            HistoryLimit = _data.HistoryLimit,
            ConfigVersion = _data.ConfigVersion,
            AgentApiKeyConfigured = !string.IsNullOrWhiteSpace(_data.AgentApiKeyHash),
            Ad = GetAdStatus(),
            ServerTimeUtc = DateTimeOffset.UtcNow
        });
    }
}

internal sealed class StoreValidationException : Exception
{
    public StoreValidationException(string message) : base(message)
    {
    }
}

internal sealed class StoreNotFoundException : Exception
{
    public StoreNotFoundException(string message) : base(message)
    {
    }
}
