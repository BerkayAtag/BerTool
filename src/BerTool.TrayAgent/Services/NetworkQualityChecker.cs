using BerTool.TrayAgent.Logging;
using BerTool.TrayAgent.Models;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BerTool.TrayAgent.Services;

internal sealed class NetworkQualityChecker
{
    private static readonly object InternetSpeedLock = new();
    private static readonly TimeSpan InternetSpeedCacheDuration = TimeSpan.FromMinutes(10);

    private const int DownloadAttemptTimeoutSeconds = 15;
    private const int DownloadBufferBytes = 64 * 1024;
    private const long MinimumSampleBytes = 512 * 1024;
    private const long MaxSampleBytes = 16 * 1024 * 1024;
    private const string SpeedTestFilePattern = "bertool-speedtest-*.msu";

    private static readonly Uri PrimaryInternetSpeedTestUri = new(
        "http://download.windowsupdate.com/c/msdownload/update/software/secu/2021/03/windows10.0-kb5000859-x64_d3e9e3c07a849c42e2d5906fa908fb31941a1026.msu");
    private static readonly Uri FallbackInternetSpeedTestUri = new(
        "https://download.windowsupdate.com/c/msdownload/update/software/secu/2021/03/windows10.0-kb5000859-x64_d3e9e3c07a849c42e2d5906fa908fb31941a1026.msu");
    private static readonly string InternetSpeedTempDirectory = Path.Combine(
        Path.GetTempPath(),
        "BerTool",
        "NetworkSpeedTest");

    private static InternetSpeedResult? cachedInternetSpeed;
    private static DateTime cachedInternetSpeedAt = DateTime.MinValue;

    public NetworkQualityInfo GetNetworkQuality()
    {
        try
        {
            var candidate = GetPrimaryNetworkInterface();
            if (candidate is null)
            {
                return NetworkQualityInfo.CreateUnknown();
            }

            var gateways = GetGatewayAddresses(candidate).ToArray();
            var internetSpeed = TryMeasureInternetSpeed();

            return new NetworkQualityInfo
            {
                HasActiveAdapter = true,
                HasGateway = gateways.Length > 0,
                AdapterName = candidate.Name,
                BandwidthMbps = candidate.Speed > 0 ? candidate.Speed / 1_000_000D : null,
                InternetSpeedMbps = internetSpeed?.Mbps,
                InternetSpeedTarget = internetSpeed?.Target ?? PrimaryInternetSpeedTestUri.Host,
                LatencyMilliseconds = internetSpeed?.FirstByteLatencyMilliseconds,
                LatencyTarget = internetSpeed is null
                    ? $"{PrimaryInternetSpeedTestUri.Host} ilk byte"
                    : $"{internetSpeed.Target} ilk byte",
                CheckedAt = DateTime.Now
            };
        }
        catch (Exception exception)
        {
            Logger.Error("Network kalite bilgisi alınamadı.", exception);
            return NetworkQualityInfo.CreateUnknown();
        }
    }

    private static NetworkInterface? GetPrimaryNetworkInterface()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsUsableNetworkInterface)
            .Select(networkInterface => new
            {
                NetworkInterface = networkInterface,
                Score = GetNetworkInterfaceScore(networkInterface)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.NetworkInterface.Speed)
            .Select(candidate => candidate.NetworkInterface)
            .FirstOrDefault();
    }

    private static bool IsUsableNetworkInterface(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up ||
            networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            return false;
        }

        try
        {
            return networkInterface.GetIPProperties().UnicastAddresses.Any(address =>
                address.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address.Address));
        }
        catch (Exception exception)
        {
            Logger.Error($"Network adaptörü okunamadı: {networkInterface.Name}", exception);
            return false;
        }
    }

    private static int GetNetworkInterfaceScore(NetworkInterface networkInterface)
    {
        var score = 1;

        try
        {
            var properties = networkInterface.GetIPProperties();
            if (properties.GatewayAddresses.Any(gateway => IsUsableIpv4Address(gateway.Address)))
            {
                score += 100;
            }
        }
        catch (Exception exception)
        {
            Logger.Error($"Gateway bilgisi okunamadı: {networkInterface.Name}", exception);
        }

        if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.Wireless80211)
        {
            score += 25;
        }

        if (networkInterface.Speed > 0)
        {
            score += 10;
        }

        return score;
    }

    private static IEnumerable<string> GetGatewayAddresses(NetworkInterface networkInterface)
    {
        try
        {
            return networkInterface.GetIPProperties()
                .GatewayAddresses
                .Select(gateway => gateway.Address)
                .Where(IsUsableIpv4Address)
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            Logger.Error($"Gateway listesi alınamadı: {networkInterface.Name}", exception);
            return Array.Empty<string>();
        }
    }

    private static bool IsUsableIpv4Address(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(address) &&
            !address.Equals(IPAddress.Any) &&
            !address.Equals(IPAddress.None);
    }

    private static InternetSpeedResult? TryMeasureInternetSpeed()
    {
        try
        {
            lock (InternetSpeedLock)
            {
                if (cachedInternetSpeed is not null &&
                    DateTime.Now - cachedInternetSpeedAt < InternetSpeedCacheDuration)
                {
                    return cachedInternetSpeed;
                }
            }

            CleanSpeedTestFiles();

            var result = TryMeasureInternetSpeedAsync().GetAwaiter().GetResult();
            if (result is not null)
            {
                lock (InternetSpeedLock)
                {
                    cachedInternetSpeed = result;
                    cachedInternetSpeedAt = DateTime.Now;
                }
            }

            return result;
        }
        catch (Exception exception)
        {
            Logger.Warn($"İnternet hızı ölçümü başarısız: {exception.Message}");
            return null;
        }
    }

    private static async Task<InternetSpeedResult?> TryMeasureInternetSpeedAsync()
    {
        Directory.CreateDirectory(InternetSpeedTempDirectory);

        var primaryResult = await TryMeasureAttemptAsync(PrimaryInternetSpeedTestUri).ConfigureAwait(false);
        if (primaryResult is not null)
        {
            return primaryResult;
        }

        return await TryMeasureAttemptAsync(FallbackInternetSpeedTestUri).ConfigureAwait(false);
    }

    private static async Task<InternetSpeedResult?> TryMeasureAttemptAsync(Uri speedTestUri)
    {
        try
        {
            return await TryDownloadSpeedSampleAsync(speedTestUri).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Zaman aşımı veya iptal durumunda diğer protokol denenebilsin diye yutulur.
            Logger.Warn($"İnternet hızı ölçümü zaman aşımına uğradı: {speedTestUri.Scheme}");
            return null;
        }
        catch (Exception exception)
        {
            Logger.Warn($"İnternet hızı ölçümü başarısız ({speedTestUri.Scheme}): {exception.Message}");
            return null;
        }
    }

    private static async Task<InternetSpeedResult?> TryDownloadSpeedSampleAsync(Uri speedTestUri)
    {
        var tempFilePath = Path.Combine(
            InternetSpeedTempDirectory,
            $"bertool-speedtest-{DateTime.Now:yyyyMMddHHmmssfff}.msu");

        try
        {
            using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(DownloadAttemptTimeoutSeconds));
            var cancellationToken = cancellationSource.Token;

            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            };
            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("BerTool.TrayAgent/1.0");
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            client.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");

            using var request = new HttpRequestMessage(HttpMethod.Get, speedTestUri);
            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            request.Headers.Pragma.ParseAdd("no-cache");

            var stopwatch = Stopwatch.StartNew();
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"İnternet hızı dosyası indirilemedi ({speedTestUri.Scheme}): {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(
                tempFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                DownloadBufferBytes,
                true);

            var buffer = new byte[DownloadBufferBytes];
            long? firstByteLatencyMilliseconds = null;
            var totalBytes = 0L;
            var timedOut = false;

            try
            {
                while (totalBytes < MaxSampleBytes)
                {
                    var bytesToRead = (int)Math.Min(buffer.Length, MaxSampleBytes - totalBytes);
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    if (!firstByteLatencyMilliseconds.HasValue)
                    {
                        firstByteLatencyMilliseconds = stopwatch.ElapsedMilliseconds;
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    totalBytes += bytesRead;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Yavaş bağlantılarda süre dolduğunda elde edilen kısmi örnekle hız hesaplanır.
                timedOut = true;
            }

            stopwatch.Stop();

            if (totalBytes < MinimumSampleBytes || stopwatch.Elapsed.TotalMilliseconds <= 0)
            {
                Logger.Warn($"İnternet hızı için yeterli veri okunamadı: {totalBytes} bayt.");
                return null;
            }

            var mbps = totalBytes * 8D / stopwatch.Elapsed.TotalSeconds / 1_000_000D;
            if (timedOut)
            {
                Logger.Warn(
                    $"İnternet hızı ölçümü {DownloadAttemptTimeoutSeconds} saniyede tamamlanamadı; " +
                    $"{totalBytes} baytlık kısmi örnek kullanıldı.");
            }

            return new InternetSpeedResult(
                mbps,
                speedTestUri.Host,
                firstByteLatencyMilliseconds ?? stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            DeleteFileIfExists(tempFilePath);
        }
    }

    private static void CleanSpeedTestFiles()
    {
        try
        {
            if (!Directory.Exists(InternetSpeedTempDirectory))
            {
                return;
            }

            foreach (var filePath in Directory.EnumerateFiles(InternetSpeedTempDirectory, SpeedTestFilePattern))
            {
                DeleteFileIfExists(filePath);
            }
        }
        catch (Exception exception)
        {
            Logger.Warn($"Eski internet hızı ölçüm dosyaları temizlenemedi: {exception.Message}");
        }
    }

    private static void DeleteFileIfExists(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception exception)
        {
            Logger.Warn($"İnternet hızı ölçüm dosyası silinemedi: {filePath} - {exception.Message}");
        }
    }

    private sealed record InternetSpeedResult(double Mbps, string Target, long FirstByteLatencyMilliseconds);
}
