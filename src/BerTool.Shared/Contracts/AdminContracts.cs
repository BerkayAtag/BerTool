namespace BerTool.Shared.Contracts;

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class AdminSessionDto
{
    public bool Authenticated { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AuthenticationMethod { get; set; } = string.Empty;
    public bool AdEnabled { get; set; }
    public bool AdConfigured { get; set; }
    public bool LocalLoginEnabled { get; set; }
    public int ConfigVersion { get; set; }
    public DateTimeOffset ServerTimeUtc { get; set; }
}

public sealed class DashboardSummaryDto
{
    public int Total { get; set; }
    public int Healthy { get; set; }
    public int Risky { get; set; }
    public int Offline { get; set; }
    public int OutdatedConfiguration { get; set; }
}

public sealed class MachineSummaryDto
{
    public string MachineId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string DomainOrWorkgroup { get; set; } = string.Empty;
    public string LoggedInUser { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string ComputerModel { get; set; } = string.Empty;
    public List<string> IpAddresses { get; set; } = new();
    public string Status { get; set; } = "offline";
    public string StatusText { get; set; } = "Çevrimdışı";
    public bool IsHealthy { get; set; }
    public bool IsOffline { get; set; }
    public List<string> RiskReasons { get; set; } = new();
    public List<ProductStatusDto> ProductStatuses { get; set; } = new();
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public DateTimeOffset CollectedAtUtc { get; set; }
    public int ConfigVersion { get; set; }
    public bool IsConfigCurrent { get; set; }
    public string AgentVersion { get; set; } = string.Empty;
}

public sealed class ProductFilterDto
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int UnhealthyMachineCount { get; set; }
}

public sealed class DashboardResponseDto
{
    public DashboardSummaryDto Summary { get; set; } = new();
    public int FilteredCount { get; set; }
    public List<MachineSummaryDto> Machines { get; set; } = new();
    public List<ProductFilterDto> Products { get; set; } = new();
    public DateTimeOffset GeneratedAtUtc { get; set; }
}

public sealed class MachineHistoryEntryDto
{
    public DateTimeOffset CollectedAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public bool IsHealthy { get; set; }
    public List<string> RiskReasons { get; set; } = new();
    public int ConfigVersion { get; set; }
}

public sealed class MachineDetailDto
{
    public MachineSummaryDto Summary { get; set; } = new();
    public AgentReportDto LatestReport { get; set; } = new();
    public List<MachineHistoryEntryDto> History { get; set; } = new();
}

public sealed class ProductUpsertRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public List<string> ServiceNames { get; set; } = new();
    public ServiceMatchMode MatchMode { get; set; } = ServiceMatchMode.All;
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
}

public sealed class AdminSettingsDto
{
    public string AdminUsername { get; set; } = "admin";
    public int ReportIntervalSeconds { get; set; } = 300;
    public int OfflineThresholdMinutes { get; set; } = 15;
    public int HistoryLimit { get; set; } = 50;
    public int ConfigVersion { get; set; }
    public bool AgentApiKeyConfigured { get; set; }
    public AdStatusDto Ad { get; set; } = new();
    public DateTimeOffset ServerTimeUtc { get; set; }
}

public sealed class AdStatusDto
{
    public bool Enabled { get; set; }
    public bool Configured { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public int ConfiguredPort { get; set; }
    public bool UseSsl { get; set; }
    public bool UseStartTls { get; set; }
    public bool AllowInsecureLdap { get; set; }
    public bool IgnoreCertificateErrors { get; set; }
    public bool InsecureTransport { get; set; }
    public bool CertificateValidationDisabled { get; set; }
    public string Encryption { get; set; } = "Yok";
    public string BaseDn { get; set; } = string.Empty;
    public string BindDn { get; set; } = string.Empty;
    public string AdminGroup { get; set; } = string.Empty;
    public bool BindAccountConfigured { get; set; }
    public bool AllowLocalFallback { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public string? ConfigurationError { get; set; }
}

public sealed class AdSettingsUpdateRequest
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool UseSsl { get; set; } = true;
    public bool UseStartTls { get; set; }
    public string BaseDn { get; set; } = string.Empty;
    public string BindDn { get; set; } = string.Empty;

    /// <summary>
    /// Boş bırakılırsa kayıtlı hizmet hesabı şifresi korunur. Şifre hiçbir
    /// zaman istemciye geri gönderilmez.
    /// </summary>
    public string BindPassword { get; set; } = string.Empty;

    public string AdminGroup { get; set; } = string.Empty;
    public bool AllowLocalFallback { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class AdConnectionTestResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? AdminGroupDn { get; set; }
    public long ElapsedMilliseconds { get; set; }
}

public sealed class UpdateAdminSettingsRequest
{
    public int ReportIntervalSeconds { get; set; } = 300;
    public int OfflineThresholdMinutes { get; set; } = 15;
    public int HistoryLimit { get; set; } = 50;
}

public sealed class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public sealed class RotateAgentKeyResponse
{
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class ApiErrorDto
{
    public string Message { get; set; } = string.Empty;
}

public sealed class ApiMessageDto
{
    public string Message { get; set; } = string.Empty;
}
