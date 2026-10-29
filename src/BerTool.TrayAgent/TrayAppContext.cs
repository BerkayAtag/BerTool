using BerTool.TrayAgent.Logging;
using BerTool.TrayAgent.Models;
using BerTool.TrayAgent.Reporting;
using BerTool.TrayAgent.Services;
using BerTool.TrayAgent.Utilities;

namespace BerTool.TrayAgent;

internal sealed class TrayAppContext : ApplicationContext
{
    private const int RefreshIntervalMilliseconds = 45 * 1000;

    private readonly NotifyIcon _notifyIcon;
    private readonly MainPopupForm _popupForm;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly TrayStatusEvaluator _statusEvaluator;
    private readonly AgentSettingsProvider _settingsProvider;
    private readonly AgentApiClient _apiClient;
    private readonly AgentConfigurationProvider _configurationProvider;
    private readonly AgentReportingService _reportingService;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Icon _healthyIcon;
    private readonly Icon _riskIcon;
    private readonly object _refreshLock = new();

    private bool _isRefreshing;
    private bool _isExiting;
    private HealthStatus? _latestStatus;

    public TrayAppContext()
    {
        _settingsProvider = new AgentSettingsProvider();
        _apiClient = new AgentApiClient(_settingsProvider.Settings);
        _configurationProvider = new AgentConfigurationProvider(_apiClient, _settingsProvider.Settings);
        _reportingService = new AgentReportingService(
            _apiClient,
            _settingsProvider.Settings,
            _configurationProvider);

        _statusEvaluator = new TrayStatusEvaluator(
            new ServiceHealthChecker(),
            new BitLockerChecker(),
            new SystemInfoProvider(),
            new NetworkQualityChecker(),
            _configurationProvider);

        _healthyIcon = IconFactory.CreateHealthyIcon();
        _riskIcon = IconFactory.CreateRiskIcon();

        _popupForm = new MainPopupForm();
        _popupForm.SetReportingStatus(_reportingService.GetStatusText(), _reportingService.HasError);

        _notifyIcon = new NotifyIcon
        {
            Icon = _riskIcon,
            Text = "Güvende değilsiniz",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        _notifyIcon.MouseClick += NotifyIconOnMouseClick;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMilliseconds };
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync().ConfigureAwait(true);
        _refreshTimer.Start();

        // İlk durum yenilemesi sunucu yapılandırmasını da çeker. Böylece ilk
        // rapor, yerleşik varsayılanlar yerine sunucudaki güncel ürün tanımlarıyla
        // oluşturulur. Ayrı bir başlatma görevi çalıştırmak iki isteğin
        // yarışmasına ve eski yapılandırmayla rapor gönderilmesine yol açıyordu.
        _ = RefreshStatusAsync();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Durum Penceresini Aç", null, (_, _) => ShowPopup());
        menu.Items.Add("Durum Penceresini Gizle", null, (_, _) => _popupForm.Hide());
        menu.Items.Add("Durumu Yenile", null, async (_, _) => await RefreshStatusAsync().ConfigureAwait(true));
        menu.Items.Add("Sunucuya Raporla", null, async (_, _) => await ReportNowAsync().ConfigureAwait(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => ExitThread());
        return menu;
    }

    private async Task RefreshStatusAsync()
    {
        // Aynı anda birden fazla ölçüm çalıştırıp UI'yi yormamak için tekil refresh kilidi.
        lock (_refreshLock)
        {
            if (_isRefreshing)
            {
                return;
            }

            _isRefreshing = true;
        }

        HealthStatus? statusToReport = null;
        var configurationChanged = false;
        try
        {
            if (_reportingService.IsConfigured)
            {
                try
                {
                    configurationChanged = await _configurationProvider
                        .EnsureFreshAsync(force: false, _lifetimeCancellation.Token)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
                {
                    return;
                }
            }

            var status = await Task.Run(_statusEvaluator.Evaluate).ConfigureAwait(true);
            if (_isExiting)
            {
                return;
            }

            _latestStatus = status;
            statusToReport = status;
            ApplyStatus(status);
        }
        catch (Exception exception)
        {
            Logger.Error("Durum yenileme işlemi sırasında hata oluştu.", exception);
            if (_isExiting)
            {
                return;
            }

            _latestStatus = HealthStatus.CreateFallback("Durum bilgisi alınamadı.");
            ApplyStatus(_latestStatus);
        }
        finally
        {
            lock (_refreshLock)
            {
                _isRefreshing = false;
            }
        }

        if (statusToReport is not null && !_isExiting)
        {
            try
            {
                // Sunucu ayarları veya ürün tanımları değiştiyse bir sonraki
                // planlı raporu beklemeden hemen raporla. Böylece konsoldan
                // yapılan değişikliklerin etkisi anında doğrulanabilir.
                await (configurationChanged
                        ? _reportingService.ReportNowAsync(statusToReport, _lifetimeCancellation.Token)
                        : _reportingService.ReportIfDueAsync(statusToReport, _lifetimeCancellation.Token))
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Logger.Error("Sunucu raporlama işlemi sırasında hata oluştu.", exception);
            }

            if (!_isExiting)
            {
                _popupForm.SetReportingStatus(_reportingService.GetStatusText(), _reportingService.HasError);
            }
        }
    }

    private async Task ReportNowAsync()
    {
        if (!_reportingService.IsConfigured)
        {
            MessageBox.Show(
                "Sunucu raporlaması yapılandırılmadı.\n\n" +
                $"Ayar dosyası: {_settingsProvider.SettingsFilePath}\n\n" +
                "serverUrl ve apiKey değerlerini tanımladıktan sonra uygulamayı yeniden başlatın.",
                "BerTool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var status = _latestStatus;
        if (status is null)
        {
            MessageBox.Show(
                "Durum bilgisi henüz hazır değil. Lütfen birkaç saniye sonra tekrar deneyin.",
                "BerTool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            var success = await _reportingService
                .ReportNowAsync(status, _lifetimeCancellation.Token)
                .ConfigureAwait(true);

            _popupForm.SetReportingStatus(_reportingService.GetStatusText(), _reportingService.HasError);
            _notifyIcon.ShowBalloonTip(
                3000,
                "BerTool",
                success ? "Durum sunucuya raporlandı." : "Durum sunucuya gönderilemedi. Ayrıntılar log dosyasında.",
                success ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Logger.Error("Manuel sunucu raporu gönderilemedi.", exception);
            MessageBox.Show(
                "Sunucu raporu gönderilemedi. Ayrıntılar log dosyasında.",
                "BerTool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ApplyStatus(HealthStatus status)
    {
        if (_isExiting)
        {
            return;
        }

        _notifyIcon.Icon = status.IsHealthy ? _healthyIcon : _riskIcon;
        _notifyIcon.Text = status.TooltipText;
        _popupForm.Render(status);
    }

    private void NotifyIconOnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            RequestShowPopup();
        }
    }

    private void RequestShowPopup()
    {
        if (_isExiting || _popupForm.IsDisposed)
        {
            return;
        }

        ShowPopup();
    }

    private void ShowPopup()
    {
        if (_isExiting || _popupForm.IsDisposed)
        {
            return;
        }

        if (_latestStatus is null)
        {
            // Yer tutucu yalnızca gösterilir; _latestStatus'a yazılırsa
            // "Sunucuya Raporla" bu sahte riskli durumu sunucuya gönderir.
            _popupForm.Render(HealthStatus.CreateFallback("İlk veri henüz yüklenmedi."));
        }

        _popupForm.SetReportingStatus(_reportingService.GetStatusText(), _reportingService.HasError);
        _popupForm.ShowNear(Cursor.Position);
    }

    protected override void ExitThreadCore()
    {
        _isExiting = true;

        Logger.Info("Uygulama kapatılıyor.");

        _lifetimeCancellation.Cancel();
        _refreshTimer.Stop();
        _refreshTimer.Dispose();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        _popupForm.AllowClose = true;
        _popupForm.Dispose();

        _reportingService.Dispose();
        _apiClient.Dispose();
        _lifetimeCancellation.Dispose();

        _healthyIcon.Dispose();
        _riskIcon.Dispose();

        base.ExitThreadCore();
    }
}
