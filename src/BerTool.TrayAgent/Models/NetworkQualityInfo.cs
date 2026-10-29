using System.Globalization;

namespace BerTool.TrayAgent.Models;

internal sealed class NetworkQualityInfo
{
    public bool HasActiveAdapter { get; init; }
    public bool HasGateway { get; init; }
    public string AdapterName { get; init; } = "Bilinmiyor";
    public double? BandwidthMbps { get; init; }
    public double? InternetSpeedMbps { get; init; }
    public string InternetSpeedTarget { get; init; } = "download.windowsupdate.com";
    public long? LatencyMilliseconds { get; init; }
    public string LatencyTarget { get; init; } = "download.windowsupdate.com ilk byte";
    public DateTime CheckedAt { get; init; } = DateTime.Now;

    public NetworkQualityLevel BandwidthLevel
    {
        get
        {
            if (!HasActiveAdapter)
            {
                return NetworkQualityLevel.Critical;
            }

            if (!BandwidthMbps.HasValue)
            {
                return NetworkQualityLevel.Unknown;
            }

            if (BandwidthMbps.Value < 5)
            {
                return NetworkQualityLevel.Critical;
            }

            if (BandwidthMbps.Value <= 10)
            {
                return NetworkQualityLevel.Warning;
            }

            return NetworkQualityLevel.Good;
        }
    }

    public NetworkQualityLevel InternetSpeedLevel
    {
        get
        {
            if (!HasActiveAdapter)
            {
                return NetworkQualityLevel.Critical;
            }

            if (!InternetSpeedMbps.HasValue)
            {
                return NetworkQualityLevel.Unknown;
            }

            if (InternetSpeedMbps.Value < 5)
            {
                return NetworkQualityLevel.Critical;
            }

            if (InternetSpeedMbps.Value <= 10)
            {
                return NetworkQualityLevel.Warning;
            }

            return NetworkQualityLevel.Good;
        }
    }

    public NetworkQualityLevel DisplayLevel => InternetSpeedLevel == NetworkQualityLevel.Unknown
        ? BandwidthLevel
        : InternetSpeedLevel;

    public string BandwidthText
    {
        get
        {
            if (!HasActiveAdapter)
            {
                return "Offline";
            }

            if (!BandwidthMbps.HasValue)
            {
                return "Ölçülemedi";
            }

            return FormatSpeed(BandwidthMbps.Value);
        }
    }

    public string InternetSpeedText
    {
        get
        {
            if (!HasActiveAdapter)
            {
                return "Offline";
            }

            if (!InternetSpeedMbps.HasValue)
            {
                return "Ölçülemedi";
            }

            return FormatSpeed(InternetSpeedMbps.Value);
        }
    }

    public string LatencyText => LatencyMilliseconds.HasValue
        ? $"{LatencyMilliseconds.Value} ms"
        : HasActiveAdapter
            ? "Ölçülemedi"
            : "Offline";

    public static NetworkQualityInfo CreateUnknown()
    {
        return new NetworkQualityInfo
        {
            HasActiveAdapter = false,
            HasGateway = false,
            AdapterName = "Bilinmiyor",
            BandwidthMbps = null,
            InternetSpeedMbps = null,
            InternetSpeedTarget = "download.windowsupdate.com",
            LatencyMilliseconds = null,
            LatencyTarget = "download.windowsupdate.com ilk byte",
            CheckedAt = DateTime.Now
        };
    }

    private static string FormatSpeed(double mbps)
    {
        if (mbps >= 1000)
        {
            var gbps = mbps / 1000D;
            var gbpsFormat = gbps >= 100 ? "0" : gbps >= 10 ? "0.#" : "0.##";
            return $"{gbps.ToString(gbpsFormat, CultureInfo.InvariantCulture)} Gbps";
        }

        var mbpsFormat = mbps >= 100 ? "0" : "0.0";
        return $"{mbps.ToString(mbpsFormat, CultureInfo.InvariantCulture)} Mbps";
    }
}