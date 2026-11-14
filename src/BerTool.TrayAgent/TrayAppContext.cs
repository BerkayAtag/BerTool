using BerTool.TrayAgent.Logging;
using BerTool.TrayAgent.Models;
using BerTool.TrayAgent.Reporting;
using BerTool.TrayAgent.Services;
using BerTool.TrayAgent.Utilities;

namespace BerTool.TrayAgent;

internal sealed class TrayAppContext : ApplicationContext
{
    private const int RefreshIntervalMilliseconds = 45 * 1000;

    // WMI veya servis denetimi askıda kalırsa yenileme sonsuza kadar
    // beklemesin; zaman aşımında durum "alınamadı" gösterilir ve bir sonraki
    // yenileme yeniden dener. Eskiden yenileme kilidi kalıcı olarak takılıyordu.
    private static readonly TimeSpan EvaluateTimeout = TimeSpan.FromMinutes(2);

    // Bundan eski bir durum sunucuya "güncel" diye gönderilmez. Yenileme
    // takıldığında rapor zamanlayıcısı eski anlık görüntüyü taze zaman
    // damgasıyla göndermeye devam ediyordu; makine konsolda canlı görünüyordu.
    private static readonly TimeSpan MaxReportableStatusAge = TimeSpan.FromMinutes(5);

    private readonly NotifyIcon _notifyIcon;
    private readonly MainPopupForm _popupForm;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _reportTimer;
    private readonly SynchronizationContext _uiContext;
    private readonly EventWaitHandle _showSignal;
    private readonly RegisteredWaitHandle _showSignalRegistration;
    private readonly TrayStatusEvaluator _statusEvaluator;
    private readonly AgentSettingsProvider _settingsProvider;
    private readonly AgentApiClient _apiClient;
    private readonly AgentConfigurationProvider _configurationProvider;
    private readonly AgentReportingService _reportingService;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Icon _healthyIcon;
    private readonly Icon _riskIcon;
    private readonly Icon _pendingIcon;
    private readonly object _refreshLock = new();

    private bool _isRefreshing;
    private bool _isExiting;
    private HealthStatus? _latestStatus;

    public TrayAppContext(bool showOnStart = false)
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
        _pendingIcon = IconFactory.CreatePendingIcon();

        _popupForm = new MainPopupForm();
        _popupForm.SetReportingStatus(_reportingService.GetStatusText(), _reportingService.HasError);
        _popupForm.ExitRequested += (_, _) =>
        {
            if (!_isExiting)
            {
                Logger.Info("Pencere sistem tarafından kapatılmak istendi; uygulama kapatılıyor.");
                ExitThread();
            }
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = _pendingIcon,
            Text = "BerTool: durum kontrol ediliyor...",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        _notifyIcon.MouseClick += NotifyIconOnMouseClick;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMilliseconds };
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync().ConfigureAwait(true);
        _refreshTimer.Start();

        // Raporlar 45 saniyelik yenilemeye bağlı kalmasın diye ayrı bir
        // zamanlayıcıyla, sunucunun bildirdiği anda gönderilir. Aksi halde 30 sn
        // olarak ayarlanan rapor aralığı 45 sn, 60 sn ise 90 sn olarak uygulanıyordu.
        _reportTimer = new System.Windows.Forms.Timer();
        _reportTimer.Tick += async (_, _) => await ReportTimerTickAsync().ConfigureAwait(true);

        // Form oluşturulduktan sonra WinForms eşitleme bağlamı hazırdır; başka
        // iş parçacıklarından gelen "pencereyi aç" isteği bununla UI'ye aktarılır.
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _showSignal = SingleInstanceSignal.CreateListener();
        _showSignalRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showSignal,
            (_, _) => _uiContext.Post(_ => ShowPopupNearTray(), null),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        if (showOnStart)
        {
            _uiContext.Post(_ => ShowPopupNearTray(), null);
        }

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
        menu.Items.Add("Durumu Yenile", null, async (_, _) => await ManualRefreshAsync().ConfigureAwait(true));
        menu.Items.Add("Sunucuya Raporla", null, async (_, _) => await ReportNowAsync().ConfigureAwait(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => ExitThread());
        return menu;
    }

    private async Task ManualRefreshAsync()
    {
        // Yenileme zaten sürüyorsa istek eskiden sessizce yok sayılıyordu.
        if (!await RefreshStatusAsync().ConfigureAwait(true) && !_isExiting)
        {
            _notifyIcon.ShowBalloonTip(
                2500,
                "BerTool",
                "Durum zaten yenileniyor; birkaç saniye içinde güncellenecek.",
                ToolTipIcon.Info);
        }
    }

    private bool IsReportable(HealthStatus? status)
    {
        return status is not null &&
            !status.IsFallback &&
            DateTime.Now - status.CheckedAt < MaxReportableStatusAge;
    }

    /// <returns>Yenileme bu çağrıda yapıldıysa true; zaten sürüyorsa false.</returns>
    private async Task<bool> RefreshStatusAsync()
    {
        // Aynı anda birden fazla ölçüm çalıştırıp UI'yi yormamak için tekil refresh kilidi.
        lock (_refreshLock)
        {
            if (_isRefreshing)
            {
                return false;
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
                    return true;
                }
            }

            var status = await Task.Run(_statusEvaluator.Evaluate)
                .WaitAsync(EvaluateTimeout)
                .ConfigureAwait(true);
            if (_isExiting)
            {
                return true;
            }

            ApplyStatus(status);
            if (!status.IsFallback)
            {
                _latestStatus = status;
                statusToReport = status;
            }
        }
        catch (Exception exception)
        {
            Logger.Error(
                exception is TimeoutException
                    ? $"Durum denetimi {EvaluateTimeout.TotalSeconds:0} saniyede tamamlanmadı (WMI veya servis yöneticisi yanıt vermiyor)."
                    : "Durum yenileme işlemi sırasında hata oluştu.",
                exception);
            if (_isExiting)
            {
                return true;
            }

            // Yer tutucu yalnızca gösterilir; raporlanabilir son durum olarak
            // saklanmaz (sunucuya boş servis listesi gitmesin).
            ApplyStatus(HealthStatus.CreateFallback("Durum bilgisi alınamadı."));
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

        ScheduleNextReport();
        return true;
    }

    /// <summary>
    /// Rapor zamanlayıcısını bir sonraki planlı rapora kurar. Her rapor
    /// denemesinden (başarılı ya da başarısız) sonra çağrılır.
    /// </summary>
    private void ScheduleNextReport()
    {
        if (_isExiting)
        {
            return;
        }

        _reportTimer.Stop();

        // İlk durum hesaplanmadan gönderilecek bir şey yoktur; yenileme
        // tamamlandığında yeniden planlanır.
        if (!_reportingService.IsConfigured || _latestStatus is null)
        {
            return;
        }

        var delay = ReportSchedule.GetDelay(_reportingService.NextReportAtUtc, DateTimeOffset.UtcNow);
        _reportTimer.Interval = Math.Max(1, (int)Math.Ceiling(delay.TotalMilliseconds));
        _reportTimer.Start();
    }

    private async Task ReportTimerTickAsync()
    {
        _reportTimer.Stop();
        if (_isExiting)
        {
            return;
        }

        var status = _latestStatus;
        if (status is not null && !IsReportable(status))
        {
            Logger.Warn(
                $"Son durum {status.CheckedAt:HH:mm:ss} tarihli ve çok eski; durum yenilemesi tamamlanana kadar rapor gönderilmiyor.");
        }
        else if (status is not null)
        {
            try
            {
                // Durum en fazla bir yenileme aralığı (45 sn) kadar eskidir.
                await _reportingService
                    .ReportIfDueAsync(status, _lifetimeCancellation.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_isExiting)
            {
                return;
            }
            catch (ObjectDisposedException) when (_isExiting)
            {
                return;
            }
            catch (Exception exception)
            {
                Logger.Error("Planlı sunucu raporu gönderilirken hata oluştu.", exception);
            }

            if (!_isExiting)
            {
                _popupForm.SetReportingStatus(_reportingService.GetStatusText(), _reportingService.HasError);
            }
        }

        ScheduleNextReport();
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

        // Elle raporlamada önce durum yenilenir; eskiden 45 sn'ye kadar eski
        // anlık görüntü gönderiliyordu. Yenileme zaten sürüyorsa onun sonucu
        // beklenmez, mevcut güncel durum kullanılır.
        await RefreshStatusAsync().ConfigureAwait(true);
        if (_isExiting)
        {
            return;
        }

        var status = _latestStatus;
        if (!IsReportable(status))
        {
            MessageBox.Show(
                "Durum bilgisi henüz hazır değil veya alınamadı. Lütfen birkaç saniye sonra tekrar deneyin.",
                "BerTool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            var success = await _reportingService
                .ReportNowAsync(status!, _lifetimeCancellation.Token)
                .ConfigureAwait(true);

            _popupForm.SetReportingStatus(_reportingService.GetStatusText(), _reportingService.HasError);
            ScheduleNextReport();
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

        _notifyIcon.Icon = status.IsFallback ? _pendingIcon : status.IsHealthy ? _healthyIcon : _riskIcon;
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

    /// <summary>
    /// Pencereyi görev çubuğu bildirim alanının yanında açar ve öne getirir.
    /// Başlat menüsü kısayolu veya ikinci başlatma gibi fare konumunun tray
    /// simgesinde olmadığı durumlarda kullanılır.
    /// </summary>
    private void ShowPopupNearTray()
    {
        if (_isExiting || _popupForm.IsDisposed)
        {
            return;
        }

        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        ShowPopup(new Point(workingArea.Right - 16, workingArea.Bottom - 6));
        _popupForm.Activate();
    }

    private void ShowPopup(Point? anchor = null)
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
        _popupForm.ShowNear(anchor ?? Cursor.Position);
    }

    protected override void ExitThreadCore()
    {
        _isExiting = true;

        Logger.Info("Uygulama kapatılıyor.");

        _lifetimeCancellation.Cancel();
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _reportTimer.Stop();
        _reportTimer.Dispose();

        _showSignalRegistration.Unregister(null);
        _showSignal.Dispose();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        _popupForm.AllowClose = true;
        _popupForm.Dispose();

        _reportingService.Dispose();
        _apiClient.Dispose();
        _lifetimeCancellation.Dispose();

        _healthyIcon.Dispose();
        _riskIcon.Dispose();
        _pendingIcon.Dispose();

        base.ExitThreadCore();
    }
}
