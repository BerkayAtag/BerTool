using BerTool.TrayAgent.Logging;
using BerTool.TrayAgent.Models;

namespace BerTool.TrayAgent.Reporting;

internal sealed class AgentReportingService : IDisposable
{
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AuthenticationRetryInterval = TimeSpan.FromMinutes(10);

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _reportLock = new(1, 1);
    private readonly AgentApiClient _apiClient;
    private readonly AgentSettings _settings;
    private readonly AgentConfigurationProvider _configurationProvider;

    private DateTimeOffset _nextReportAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastSuccessAtUtc;
    private string? _lastError;
    private bool _disposed;

    public AgentReportingService(
        AgentApiClient apiClient,
        AgentSettings settings,
        AgentConfigurationProvider configurationProvider)
    {
        _apiClient = apiClient;
        _settings = settings;
        _configurationProvider = configurationProvider;
    }

    public bool IsConfigured => _settings.IsConfigured;

    public bool HasError
    {
        get
        {
            lock (_stateLock)
            {
                return _lastError is not null;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_stateLock)
            {
                return _lastError;
            }
        }
    }

    public async Task<bool> ReportIfDueAsync(HealthStatus status, CancellationToken cancellationToken)
    {
        if (!_settings.IsConfigured)
        {
            return false;
        }

        lock (_stateLock)
        {
            if (DateTimeOffset.UtcNow < _nextReportAtUtc)
            {
                return false;
            }
        }

        return await ReportCoreAsync(status, waitForLock: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ReportNowAsync(HealthStatus status, CancellationToken cancellationToken)
    {
        if (!_settings.IsConfigured)
        {
            Logger.Warn("Sunucuya rapor gönderilemedi: ajan sunucu ayarı yapılandırılmadı.");
            return false;
        }

        return await ReportCoreAsync(status, waitForLock: true, cancellationToken).ConfigureAwait(false);
    }

    public string GetStatusText()
    {
        if (!_settings.IsConfigured)
        {
            return "Sunucu raporu: yapılandırılmadı";
        }

        lock (_stateLock)
        {
            if (_lastError is not null)
            {
                return $"Sunucu raporu: hata - {Shorten(_lastError, 48)}";
            }

            if (_lastSuccessAtUtc is null)
            {
                return "Sunucu raporu: ilk gönderim bekleniyor";
            }

            var lastSuccess = _lastSuccessAtUtc.Value.ToLocalTime();
            var next = _nextReportAtUtc == DateTimeOffset.MinValue
                ? "hemen"
                : _nextReportAtUtc.ToLocalTime().ToString("HH:mm");

            return $"Sunucu raporu: {lastSuccess:HH:mm} · sonraki {next}";
        }
    }

    private async Task<bool> ReportCoreAsync(
        HealthStatus status,
        bool waitForLock,
        CancellationToken cancellationToken)
    {
        var lockTaken = waitForLock
            ? await WaitForLockAsync(cancellationToken).ConfigureAwait(false)
            : _reportLock.Wait(0);

        if (!lockTaken)
        {
            return false;
        }

        try
        {
            var configuration = _configurationProvider.Current;
            var report = AgentReportBuilder.Build(status, configuration);

            try
            {
                var acknowledgement = await _apiClient
                    .SendReportAsync(report, cancellationToken)
                    .ConfigureAwait(false);

                var intervalSeconds = acknowledgement.NextReportAfterSeconds > 0
                    ? acknowledgement.NextReportAfterSeconds
                    : configuration.ReportIntervalSeconds;
                intervalSeconds = Math.Clamp(intervalSeconds, 30, 86_400);

                lock (_stateLock)
                {
                    _lastSuccessAtUtc = DateTimeOffset.UtcNow;
                    _nextReportAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(intervalSeconds);
                    _lastError = null;
                }

                _configurationProvider.NotifyServerConfigVersion(acknowledgement.ConfigVersion);

                Logger.Info(
                    $"Sunucu raporu gönderildi. Makine: {report.MachineName}, " +
                    $"durum: {(report.IsHealthy ? "Sağlıklı" : "Riskli")}, " +
                    $"sonraki rapor: {intervalSeconds} sn sonra.");
                return true;
            }
            catch (AgentAuthenticationException exception)
            {
                lock (_stateLock)
                {
                    _nextReportAtUtc = DateTimeOffset.UtcNow + AuthenticationRetryInterval;
                    _lastError = exception.Message;
                }

                Logger.Error("Sunucu ajan API anahtarını reddetti; raporlama durduruldu.", exception);
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lock (_stateLock)
                {
                    _nextReportAtUtc = DateTimeOffset.UtcNow + FailureRetryInterval;
                    _lastError = exception.Message;
                }

                Logger.Warn($"Sunucu raporu gönderilemedi, {FailureRetryInterval.TotalMinutes:0} dakika sonra yeniden denenecek: {exception.Message}");
                return false;
            }
        }
        finally
        {
            _reportLock.Release();
        }
    }

    private async Task<bool> WaitForLockAsync(CancellationToken cancellationToken)
    {
        await _reportLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string Shorten(string value, int maxLength)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength] + "...";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // SemaphoreSlim burada bilinçli olarak dispose edilmez. Uygulama kapanırken
        // devam eden bir rapor gönderimi finally bloğunda Release() çağırabilir;
        // semaforu dispose etmek bu durumda ObjectDisposedException üretir.
        // SemaphoreSlim yalnızca yönetilen bellek kullandığı için süreç kapanışında
        // çöp toplayıcı tarafından temizlenmesi yeterlidir.
    }
}
