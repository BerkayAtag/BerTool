using BerTool.Shared.Contracts;

namespace BerTool.Shared.Products;

/// <summary>
/// İlk kurulumda kullanılan varsayılan ürün tanımları.
/// Sunucu bu tanımları başlangıç verisi olarak kullanır; ajan sunucuya
/// ulaşamadığında da aynı tanımlarla yerel kontrol yapabilir.
/// </summary>
public static class DefaultProductCatalog
{
    public const int DefaultReportIntervalSeconds = 300;
    public const int DefaultOfflineThresholdMinutes = 15;
    public const int DefaultHistoryLimit = 50;
    public const int DefaultConfigVersion = 1;

    public static IReadOnlyList<ProductDefinitionDto> Create()
    {
        var updatedAt = DateTimeOffset.UnixEpoch;

        return new List<ProductDefinitionDto>
        {
            new()
            {
                Id = "bigfix",
                DisplayName = "Bigfix",
                ServiceNames = new List<string> { "BESClient" },
                MatchMode = ServiceMatchMode.All,
                Enabled = true,
                SortOrder = 10,
                UpdatedAtUtc = updatedAt
            },
            new()
            {
                Id = "trc-uzak-destek",
                DisplayName = "TRC Uzak Destek",
                ServiceNames = new List<string> { "TRCTARGET" },
                MatchMode = ServiceMatchMode.All,
                Enabled = true,
                SortOrder = 20,
                UpdatedAtUtc = updatedAt
            },
            new()
            {
                Id = "delinea",
                DisplayName = "Delinea",
                ServiceNames = new List<string> { "ArelliaAgent", "ArelliaACSvc" },
                MatchMode = ServiceMatchMode.All,
                Enabled = true,
                SortOrder = 30,
                UpdatedAtUtc = updatedAt
            },
            new()
            {
                Id = "forcepoint-dlp",
                DisplayName = "ForcePoint DLP",
                ServiceNames = new List<string> { "WSDLP" },
                MatchMode = ServiceMatchMode.All,
                Enabled = true,
                SortOrder = 40,
                UpdatedAtUtc = updatedAt
            },
            new()
            {
                Id = "zabbix-agent-2",
                DisplayName = "Zabbix Agent 2",
                ServiceNames = new List<string> { "Zabbix Agent 2", "zabbix_agent2", "ZabbixAgent2" },
                MatchMode = ServiceMatchMode.Any,
                Enabled = true,
                SortOrder = 50,
                UpdatedAtUtc = updatedAt
            },
            new()
            {
                Id = "defender-antivirus",
                DisplayName = "Defender Antivirüs",
                ServiceNames = new List<string> { "Sense", "WinDefend" },
                MatchMode = ServiceMatchMode.All,
                Enabled = true,
                SortOrder = 60,
                UpdatedAtUtc = updatedAt
            }
        };
    }

    public static AgentConfigurationDto CreateConfiguration(
        int reportIntervalSeconds = DefaultReportIntervalSeconds,
        int offlineThresholdMinutes = DefaultOfflineThresholdMinutes)
    {
        return new AgentConfigurationDto
        {
            ConfigVersion = DefaultConfigVersion,
            ReportIntervalSeconds = reportIntervalSeconds,
            OfflineThresholdMinutes = offlineThresholdMinutes,
            Products = Create().ToList()
        };
    }
}
