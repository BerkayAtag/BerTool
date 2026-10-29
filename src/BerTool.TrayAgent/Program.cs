using BerTool.TrayAgent.Services;
using BerTool.TrayAgent.Logging;
using BerTool.TrayAgent.Reporting;
using System.Diagnostics;
using System.Security;
using System.Text;

namespace BerTool.TrayAgent;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\BerTool.TrayAgent";
    private const string BitLockerTaskName = "BerTool BitLocker Cache";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
        Application.ThreadException += ApplicationOnThreadException;
        TaskScheduler.UnobservedTaskException += TaskSchedulerOnUnobservedTaskException;

        if (args.Any(argument => string.Equals(argument, "--update-bitlocker-cache", StringComparison.OrdinalIgnoreCase)))
        {
            return UpdateBitLockerCache();
        }

        if (args.Any(argument => string.Equals(argument, "--register-bitlocker-task", StringComparison.OrdinalIgnoreCase)))
        {
            return RegisterBitLockerTask();
        }

        if (args.Any(argument => string.Equals(argument, "--report-once", StringComparison.OrdinalIgnoreCase)))
        {
            return ReportOnce();
        }

        // Aynı kullanıcı oturumunda birden fazla tray ajanı çalışmasını engeller.
        // Cache güncelleme modu bu kontrolden önce çalıştığı için görev tarafından her zaman tetiklenebilir.
        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out var isFirstInstance);

        if (!isFirstInstance)
        {
            Logger.Info("BerTool zaten çalışıyor; ikinci örnek kapatıldı.");
            return 0;
        }

        try
        {
            Logger.Info("Uygulama başlatıldı.");

            ApplicationConfiguration.Initialize();
            using var trayContext = new TrayAppContext();
            Application.Run(trayContext);
            return 0;
        }
        finally
        {
            singleInstanceMutex.ReleaseMutex();
        }
    }

    private static int UpdateBitLockerCache()
    {
        try
        {
            Logger.Info("BitLocker cache güncellemesi başlatıldı.");
            return new BitLockerChecker().RefreshCache() ? 0 : 1;
        }
        catch (Exception exception)
        {
            Logger.Error("BitLocker cache güncellemesi başarısız oldu.", exception);
            return 1;
        }
    }

    private static int ReportOnce()
    {
        try
        {
            Logger.Info("Tek seferlik sunucu raporu başlatıldı.");
            var settingsProvider = new AgentSettingsProvider();
            if (!settingsProvider.Settings.IsConfigured)
            {
                Logger.Error(
                    "Tek seferlik rapor gönderilemedi: ajan sunucu ayarı yapılandırılmadı. " +
                    $"Ayar dosyası: {settingsProvider.SettingsFilePath}");
                return 2;
            }

            using var apiClient = new AgentApiClient(settingsProvider.Settings);
            var configurationProvider = new AgentConfigurationProvider(apiClient, settingsProvider.Settings);
            configurationProvider
                .EnsureFreshAsync(force: true, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var evaluator = new TrayStatusEvaluator(
                new ServiceHealthChecker(),
                new BitLockerChecker(),
                new SystemInfoProvider(),
                new NetworkQualityChecker(),
                configurationProvider);

            var status = evaluator.Evaluate();
            using var reportingService = new AgentReportingService(
                apiClient,
                settingsProvider.Settings,
                configurationProvider);

            var success = reportingService
                .ReportNowAsync(status, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Logger.Info(success
                ? "Tek seferlik sunucu raporu başarıyla gönderildi."
                : "Tek seferlik sunucu raporu gönderilemedi.");
            return success ? 0 : 1;
        }
        catch (Exception exception)
        {
            Logger.Error("Tek seferlik sunucu raporu sırasında hata oluştu.", exception);
            return 1;
        }
    }

    private static int RegisterBitLockerTask()
    {
        var tempFilePath = Path.Combine(
            Path.GetTempPath(),
            $"bertool-bitlocker-task-{Guid.NewGuid():N}.xml");

        try
        {
            var executablePath = Environment.ProcessPath ?? Application.ExecutablePath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                Logger.Error("Zamanlanmış görev için uygulama yolu bulunamadı.");
                return 1;
            }

            var xml = BuildBitLockerTaskXml(executablePath);
            File.WriteAllText(tempFilePath, xml, Encoding.Unicode);

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("/Create");
            startInfo.ArgumentList.Add("/TN");
            startInfo.ArgumentList.Add(BitLockerTaskName);
            startInfo.ArgumentList.Add("/XML");
            startInfo.ArgumentList.Add(tempFilePath);
            startInfo.ArgumentList.Add("/F");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Logger.Error("schtasks.exe başlatılamadı.");
                return 1;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();

            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                Logger.Error(
                    $"BitLocker cache görevi oluşturulamadı. Çıkış kodu: {process.ExitCode}. " +
                    $"Çıktı: {output.Trim()} {error.Trim()}");
                return 1;
            }

            Logger.Info($"BitLocker cache görevi oluşturuldu: {BitLockerTaskName}");
            return 0;
        }
        catch (Exception exception)
        {
            Logger.Error("BitLocker cache görevi oluşturulurken hata oluştu.", exception);
            return 1;
        }
        finally
        {
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch (Exception exception)
            {
                Logger.Warn($"Geçici görev dosyası silinemedi: {exception.Message}");
            }
        }
    }

    private static string BuildBitLockerTaskXml(string executablePath)
    {
        var escapedPath = SecurityElement.Escape(executablePath) ?? executablePath;
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        builder.AppendLine("<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
        builder.AppendLine("  <RegistrationInfo>");
        builder.AppendLine("    <Description>BerTool BitLocker cache update</Description>");
        builder.AppendLine("    <URI>\\BerTool BitLocker Cache</URI>");
        builder.AppendLine("  </RegistrationInfo>");
        builder.AppendLine("  <Triggers>");
        builder.AppendLine("    <LogonTrigger>");
        builder.AppendLine("      <Enabled>true</Enabled>");
        builder.AppendLine("    </LogonTrigger>");
        builder.AppendLine("  </Triggers>");
        builder.AppendLine("  <Principals>");
        builder.AppendLine("    <Principal id=\"Author\">");
        builder.AppendLine("      <UserId>S-1-5-18</UserId>");
        builder.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
        builder.AppendLine("    </Principal>");
        builder.AppendLine("  </Principals>");
        builder.AppendLine("  <Settings>");
        builder.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        builder.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        builder.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        builder.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
        builder.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
        builder.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
        builder.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
        builder.AppendLine("    <Enabled>true</Enabled>");
        builder.AppendLine("    <Hidden>false</Hidden>");
        builder.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
        builder.AppendLine("    <WakeToRun>false</WakeToRun>");
        builder.AppendLine("    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>");
        builder.AppendLine("    <Priority>7</Priority>");
        builder.AppendLine("  </Settings>");
        builder.AppendLine("  <Actions Context=\"Author\">");
        builder.AppendLine("    <Exec>");
        builder.AppendLine("      <Command>" + escapedPath + "</Command>");
        builder.AppendLine("      <Arguments>--update-bitlocker-cache</Arguments>");
        builder.AppendLine("    </Exec>");
        builder.AppendLine("  </Actions>");
        builder.AppendLine("</Task>");
        return builder.ToString();
    }

    private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new Exception("Bilinmeyen AppDomain hatası.");
        Logger.Error("Uygulama düzeyi beklenmeyen hata.", exception);
    }

    private static void ApplicationOnThreadException(object sender, ThreadExceptionEventArgs e)
    {
        Logger.Error("UI thread üzerinde beklenmeyen hata.", e.Exception);
    }

    private static void TaskSchedulerOnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("Görev zamanlayıcısında gözlemlenmeyen hata.", e.Exception);
        e.SetObserved();
    }
}
