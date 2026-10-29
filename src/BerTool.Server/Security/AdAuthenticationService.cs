using BerTool.Server.Configuration;
using BerTool.Server.Storage;
using BerTool.Shared.Contracts;
using System.DirectoryServices.Protocols;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BerTool.Server.Security;

internal enum AdAuthenticationStatus
{
    Disabled,
    Success,
    InvalidCredentials,
    NotAuthorized,
    Unavailable,
    ConfigurationError
}

internal sealed record AdAuthenticationResult(
    AdAuthenticationStatus Status,
    string Username = "",
    string DisplayName = "",
    string? Error = null);

internal sealed class AdAuthenticationService
{
    private const int InvalidCredentialsErrorCode = 49;
    private const int AccountDisabledFlag = 0x2;
    private const int AccountLockedOutFlag = 0x10;
    private const int MaxUsernameLength = 256;
    private const int MaxLdapSearchResults = 2;

    private readonly ServerStore? _store;
    private readonly AdOptions? _fixedOptions;
    private readonly ILogger<AdAuthenticationService> _logger;

    public AdAuthenticationService(ServerStore store, ILogger<AdAuthenticationService> logger)
    {
        _store = store;
        _logger = logger;
    }

    internal AdAuthenticationService(AdOptions options, ILogger<AdAuthenticationService> logger)
    {
        _fixedOptions = options ?? new AdOptions();
        _logger = logger;
    }

    internal AdAuthenticationService(ServerOptions serverOptions, ILogger<AdAuthenticationService> logger)
        : this(serverOptions.Ad ?? new AdOptions(), logger)
    {
    }

    private AdOptions CurrentOptions => _fixedOptions ?? _store?.GetAdOptions() ?? new AdOptions();

    public bool IsEnabled => CurrentOptions.Enabled;

    public bool IsConfigured => CurrentOptions.Validate() is null;

    public bool IsLocalLoginEnabled
    {
        get
        {
            var options = CurrentOptions;
            return !options.Enabled || options.AllowLocalFallback;
        }
    }

    public string Fingerprint => CurrentOptions.CreateFingerprint();

    public AdStatusDto GetStatus() => _store?.GetAdStatus() ?? CurrentOptions.ToStatus();

    public bool IsSessionAllowed(string? authenticationMethod, string? fingerprint)
    {
        var options = CurrentOptions;

        if (string.Equals(authenticationMethod, "ad", StringComparison.Ordinal))
        {
            return options.Enabled &&
                options.Validate() is null &&
                string.Equals(fingerprint, options.CreateFingerprint(), StringComparison.Ordinal);
        }

        // Eski oturumlarda kimlik yöntemi bilgisi yoktur; bunlar yerel oturum
        // sayılır. Yerel giriş (yedek erişim) kapalıysa reddedilir.
        if (string.Equals(authenticationMethod, "local", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(authenticationMethod))
        {
            return !options.Enabled || options.AllowLocalFallback;
        }

        return false;
    }

    public AdAuthenticationResult Authenticate(string username, string password)
    {
        var options = CurrentOptions;

        if (!options.Enabled)
        {
            return new AdAuthenticationResult(AdAuthenticationStatus.Disabled);
        }

        var configurationError = options.Validate();
        if (configurationError is not null)
        {
            return new AdAuthenticationResult(
                AdAuthenticationStatus.ConfigurationError,
                Error: configurationError);
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return new AdAuthenticationResult(AdAuthenticationStatus.InvalidCredentials);
        }

        if (username.Length > MaxUsernameLength || password.Length > 512)
        {
            return new AdAuthenticationResult(AdAuthenticationStatus.InvalidCredentials);
        }

        var identity = ParseIdentity(username);
        if (identity is null)
        {
            return new AdAuthenticationResult(AdAuthenticationStatus.InvalidCredentials);
        }

        LdapConnection? serviceConnection = null;
        try
        {
            serviceConnection = CreateConnection(options, options.BindDn, options.BindPassword);
        }
        catch (LdapException exception) when (exception.ErrorCode == InvalidCredentialsErrorCode)
        {
            _logger.LogError(
                "AD hizmet hesabıyla bağlantı kurulamadı (LDAP kodu 49). BindDn veya parola hatalı. Sunucu: {Host}:{Port}",
                options.Host,
                options.EffectivePort);
            return new AdAuthenticationResult(
                AdAuthenticationStatus.ConfigurationError,
                Error: "AD hizmet hesabı kimlik bilgileri doğrulanamadı.");
        }
        catch (LdapException exception)
        {
            _logger.LogError(
                exception,
                "AD hizmet hesabı bağlantısı kurulamadı. Sunucu: {Host}:{Port}, LDAP kodu: {ErrorCode}",
                options.Host,
                options.EffectivePort,
                exception.ErrorCode);
            return new AdAuthenticationResult(
                AdAuthenticationStatus.Unavailable,
                Error: "Active Directory sunucusuna ulaşılamadı.");
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "AD hizmet hesabı bağlantısı sırasında beklenmeyen hata. Sunucu: {Host}:{Port}",
                options.Host,
                options.EffectivePort);
            return new AdAuthenticationResult(
                AdAuthenticationStatus.Unavailable,
                Error: "Active Directory kimlik doğrulaması tamamlanamadı.");
        }

        using (serviceConnection)
        {
            try
            {
                var user = FindUser(options, serviceConnection, identity);
                if (user is null)
                {
                    _logger.LogInformation(
                        "AD kullanıcısı bulunamadı veya birden fazla kayıtla eşleşti. Kullanıcı: {Username}",
                        SanitizeForLog(identity.DisplayName));
                    return new AdAuthenticationResult(AdAuthenticationStatus.InvalidCredentials);
                }

                if ((user.UserAccountControl & AccountDisabledFlag) != 0 ||
                    (user.UserAccountControl & AccountLockedOutFlag) != 0)
                {
                    _logger.LogWarning(
                        "Devre dışı veya kilitli AD hesabıyla giriş denemesi. Kullanıcı: {Username}",
                        SanitizeForLog(identity.DisplayName));
                    return new AdAuthenticationResult(AdAuthenticationStatus.InvalidCredentials);
                }

                // Parola doğrulaması kullanıcının kendi kimliğiyle bind edilerek yapılır.
                try
                {
                    using var userConnection = CreateConnection(
                        options,
                        user.DistinguishedName,
                        password);

                    if (!IsMemberOfAdminGroup(options, serviceConnection, user))
                    {
                        _logger.LogWarning(
                            "AD kullanıcısı yönetici grubunda değil. Kullanıcı: {Username}, Grup: {Group}",
                            SanitizeForLog(user.SamAccountName),
                            SanitizeForLog(options.AdminGroup));
                        return new AdAuthenticationResult(
                            AdAuthenticationStatus.NotAuthorized,
                            user.SamAccountName,
                            user.DisplayName);
                    }
                }
                catch (LdapException exception) when (exception.ErrorCode == InvalidCredentialsErrorCode)
                {
                    _logger.LogWarning(
                        "AD parola doğrulaması başarısız. Kullanıcı: {Username}, LDAP kodu: {ErrorCode}",
                        SanitizeForLog(identity.DisplayName),
                        exception.ErrorCode);
                    return new AdAuthenticationResult(AdAuthenticationStatus.InvalidCredentials);
                }

                _logger.LogInformation(
                    "AD yönetici girişi doğrulandı. Kullanıcı: {Username}, Grup: {Group}",
                    SanitizeForLog(user.SamAccountName),
                    SanitizeForLog(options.AdminGroup));

                return new AdAuthenticationResult(
                    AdAuthenticationStatus.Success,
                    user.SamAccountName,
                    user.DisplayName);
            }
            catch (LdapException exception)
            {
                _logger.LogError(
                    exception,
                    "AD kullanıcı sorgusu başarısız oldu. Sunucu: {Host}:{Port}, LDAP kodu: {ErrorCode}",
                    options.Host,
                    options.EffectivePort,
                    exception.ErrorCode);
                return new AdAuthenticationResult(
                    AdAuthenticationStatus.Unavailable,
                    Error: "Active Directory sunucusuna ulaşılamadı.");
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "AD kimlik doğrulaması sırasında beklenmeyen hata. Sunucu: {Host}:{Port}",
                    options.Host,
                    options.EffectivePort);
                return new AdAuthenticationResult(
                    AdAuthenticationStatus.Unavailable,
                    Error: "Active Directory kimlik doğrulaması tamamlanamadı.");
            }
        }
    }

    public AdConnectionTestResultDto TestConnection(AdOptions? draftOptions = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var options = draftOptions?.Clone() ?? CurrentOptions;
        var validationError = options.Validate();

        if (!options.Enabled)
        {
            return new AdConnectionTestResultDto
            {
                Success = false,
                Message = "Active Directory entegrasyonu kapalı. Test için önce etkinleştirin.",
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            };
        }

        if (validationError is not null)
        {
            return new AdConnectionTestResultDto
            {
                Success = false,
                Message = validationError,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            };
        }

        try
        {
            using var connection = CreateConnection(options, options.BindDn, options.BindPassword);

            var baseRequest = new SearchRequest(
                options.BaseDn,
                "(objectClass=*)",
                SearchScope.Base,
                "distinguishedName");
            ConfigureSearchLimits(baseRequest, options, sizeLimit: 1);

            var baseResponse = (SearchResponse)connection.SendRequest(baseRequest);
            if (baseResponse.Entries.Count == 0)
            {
                return new AdConnectionTestResultDto
                {
                    Success = false,
                    Message = "BaseDn bulunamadı veya hizmet hesabının bu dizini okuma yetkisi yok.",
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                };
            }

            var groupDn = ResolveAdminGroupDn(options, connection);
            if (string.IsNullOrWhiteSpace(groupDn))
            {
                return new AdConnectionTestResultDto
                {
                    Success = false,
                    Message = "Yönetici grubu bulunamadı. Grup adını veya DN'sini kontrol edin.",
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                };
            }

            var groupRequest = new SearchRequest(
                groupDn,
                "(objectClass=*)",
                SearchScope.Base,
                "distinguishedName");
            ConfigureSearchLimits(groupRequest, options, sizeLimit: 1);
            var groupResponse = (SearchResponse)connection.SendRequest(groupRequest);
            if (groupResponse.Entries.Count == 0)
            {
                return new AdConnectionTestResultDto
                {
                    Success = false,
                    Message = "Yönetici grubu DN'si dizinde bulunamadı.",
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                };
            }

            return new AdConnectionTestResultDto
            {
                Success = true,
                Message = "AD bağlantısı, hizmet hesabı ve yönetici grubu doğrulandı.",
                AdminGroupDn = groupDn,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            };
        }
        catch (LdapException exception)
        {
            _logger.LogError(
                exception,
                "AD bağlantı testi başarısız. Sunucu: {Host}:{Port}, LDAP kodu: {ErrorCode}",
                options.Host,
                options.EffectivePort,
                exception.ErrorCode);

            return new AdConnectionTestResultDto
            {
                Success = false,
                Message = exception.ErrorCode == InvalidCredentialsErrorCode
                    ? "Hizmet hesabı kimlik bilgileri doğrulanamadı. BindDn ve parolayı kontrol edin."
                    : "AD bağlantı testi başarısız. Sunucu adresi, port, sertifika ve ağ erişimini kontrol edin.",
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            };
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "AD bağlantı testi sırasında beklenmeyen hata. Sunucu: {Host}:{Port}",
                options.Host,
                options.EffectivePort);

            return new AdConnectionTestResultDto
            {
                Success = false,
                Message = "AD bağlantı testi tamamlanamadı. Sunucu adresi, sertifika ve ağ erişimini kontrol edin.",
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            };
        }
    }

    private LdapConnection CreateConnection(AdOptions options, string bindDn, string bindPassword)
    {
        var identifier = new LdapDirectoryIdentifier(
            options.Host,
            options.EffectivePort,
            fullyQualifiedDnsHostName: false,
            connectionless: false);

        var connection = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 3, 60))
        };

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        if (options.IgnoreCertificateErrors)
        {
            _logger.LogWarning(
                "AD sertifika doğrulaması devre dışı. Bu ayar yalnızca test ortamında kullanılmalıdır. Sunucu: {Host}:{Port}",
                options.Host,
                options.EffectivePort);
            connection.SessionOptions.VerifyServerCertificate = (_, _) => true;
        }

        if (options.UseSsl)
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }
        else if (options.UseStartTls)
        {
            connection.SessionOptions.StartTransportLayerSecurity(new DirectoryControlCollection());
        }

        connection.Bind(new NetworkCredential(bindDn, bindPassword));
        return connection;
    }

    private static AdIdentity? ParseIdentity(string username)
    {
        var trimmed = username.Trim();
        if (trimmed.Length is 0 or > MaxUsernameLength ||
            trimmed.Any(char.IsControl))
        {
            return null;
        }

        var separator = trimmed.LastIndexOf('\\');
        if (separator >= 0)
        {
            if (separator == trimmed.Length - 1)
            {
                return null;
            }

            trimmed = trimmed[(separator + 1)..];
        }

        var atIndex = trimmed.IndexOf('@');
        if (atIndex == 0)
        {
            return null;
        }

        if (atIndex > 0)
        {
            var sam = trimmed[..atIndex];
            return sam.Length == 0
                ? null
                : new AdIdentity(sam, trimmed, trimmed);
        }

        return trimmed.Length == 0
            ? null
            : new AdIdentity(trimmed, null, trimmed);
    }

    private AdUser? FindUser(AdOptions options, LdapConnection connection, AdIdentity identity)
    {
        var samFilter = $"sAMAccountName={LdapFilter.Escape(identity.SamAccountName)}";
        var filter = identity.UserPrincipalName is null
            ? $"(&(objectClass=user)({samFilter}))"
            : $"(&(objectClass=user)(|({samFilter})(userPrincipalName={LdapFilter.Escape(identity.UserPrincipalName)})))";

        var request = new SearchRequest(
            options.BaseDn,
            filter,
            SearchScope.Subtree,
            "distinguishedName",
            "sAMAccountName",
            "userPrincipalName",
            "displayName",
            "cn",
            "userAccountControl",
            "objectSid",
            "primaryGroupID");
        ConfigureSearchLimits(request, options, MaxLdapSearchResults);

        var response = (SearchResponse)connection.SendRequest(request);
        if (response.Entries.Count != 1)
        {
            return null;
        }

        var entry = response.Entries[0];
        var distinguishedName = ReadString(entry, "distinguishedName");
        var samAccountName = ReadString(entry, "sAMAccountName");
        if (string.IsNullOrWhiteSpace(distinguishedName) || string.IsNullOrWhiteSpace(samAccountName))
        {
            return null;
        }

        var displayName = ReadString(entry, "displayName");
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = ReadString(entry, "cn");
        }

        var userAccountControl = 0;
        var rawControl = ReadString(entry, "userAccountControl");
        if (!int.TryParse(rawControl, out userAccountControl))
        {
            userAccountControl = 0;
        }

        var primaryGroupId = 0;
        var rawPrimaryGroup = ReadString(entry, "primaryGroupID");
        if (!int.TryParse(rawPrimaryGroup, out primaryGroupId))
        {
            primaryGroupId = 0;
        }

        return new AdUser(
            distinguishedName,
            samAccountName,
            string.IsNullOrWhiteSpace(displayName) ? samAccountName : displayName,
            userAccountControl,
            ReadBytes(entry, "objectSid"),
            primaryGroupId);
    }

    private bool IsMemberOfAdminGroup(AdOptions options, LdapConnection connection, AdUser user)
    {
        var groupDn = ResolveAdminGroupDn(options, connection);
        if (string.IsNullOrWhiteSpace(groupDn))
        {
            _logger.LogError(
                "AD yönetici grubu bulunamadı. Grup: {Group}",
                SanitizeForLog(options.AdminGroup));
            return false;
        }

        // Birincil grup üyeliği member/memberOf zincirinde görünmeyebilir.
        // tokenGroups yerine kullanıcının objectSid ve primaryGroupID değerinden
        // grup SID'si hesaplanır ve grubun SID'siyle karşılaştırılır.
        if (user.ObjectSid is { Length: >= 12 } && user.PrimaryGroupId > 0)
        {
            var groupSid = ReadGroupSid(connection, options, groupDn);
            if (groupSid is not null)
            {
                var primaryGroupSid = BuildPrimaryGroupSid(user.ObjectSid, user.PrimaryGroupId);
                if (CryptographicOperations.FixedTimeEquals(primaryGroupSid, groupSid))
                {
                    return true;
                }
            }
        }

        // LDAP_MATCHING_RULE_IN_CHAIN (1.2.840.113556.1.4.1941) iç içe grup
        // üyeliklerini de kapsar; yalnızca doğrudan memberOf kontrolü yapılmaz.
        var filter = $"(&(objectClass=group)(member:1.2.840.113556.1.4.1941:={LdapFilter.Escape(user.DistinguishedName)}))";
        var request = new SearchRequest(groupDn, filter, SearchScope.Base, "distinguishedName");
        ConfigureSearchLimits(request, options, sizeLimit: 1);
        var response = (SearchResponse)connection.SendRequest(request);
        return response.Entries.Count > 0;
    }

    private static byte[]? ReadGroupSid(
        LdapConnection connection,
        AdOptions options,
        string groupDn)
    {
        var request = new SearchRequest(groupDn, "(objectClass=*)", SearchScope.Base, "objectSid");
        ConfigureSearchLimits(request, options, sizeLimit: 1);
        var response = (SearchResponse)connection.SendRequest(request);
        return response.Entries.Count == 0
            ? null
            : ReadBytes(response.Entries[0], "objectSid");
    }

    private static byte[] BuildPrimaryGroupSid(byte[] userSid, int primaryGroupId)
    {
        var sid = (byte[])userSid.Clone();
        var offset = sid.Length - 4;
        sid[offset] = (byte)(primaryGroupId & 0xFF);
        sid[offset + 1] = (byte)((primaryGroupId >> 8) & 0xFF);
        sid[offset + 2] = (byte)((primaryGroupId >> 16) & 0xFF);
        sid[offset + 3] = (byte)((primaryGroupId >> 24) & 0xFF);
        return sid;
    }

    private string? ResolveAdminGroupDn(AdOptions options, LdapConnection connection)
    {
        var configuredGroup = options.AdminGroup.Trim();
        if (configuredGroup.Contains('=', StringComparison.Ordinal) &&
            configuredGroup.Contains(',', StringComparison.Ordinal))
        {
            return configuredGroup;
        }

        var escaped = LdapFilter.Escape(configuredGroup);
        var filter = $"(&(objectClass=group)(|(sAMAccountName={escaped})(cn={escaped})))";
        var request = new SearchRequest(
            options.BaseDn,
            filter,
            SearchScope.Subtree,
            "distinguishedName");
        ConfigureSearchLimits(request, options, MaxLdapSearchResults);

        var response = (SearchResponse)connection.SendRequest(request);
        if (response.Entries.Count != 1)
        {
            if (response.Entries.Count > 1)
            {
                _logger.LogError(
                    "AD yönetici grubu adı birden fazla grupla eşleşti. Daha belirgin bir grup adı veya DN kullanın: {Group}",
                    SanitizeForLog(options.AdminGroup));
            }

            return null;
        }

        return ReadString(response.Entries[0], "distinguishedName");
    }

    private static void ConfigureSearchLimits(SearchRequest request, AdOptions options, int sizeLimit)
    {
        request.SizeLimit = Math.Clamp(sizeLimit, 1, MaxLdapSearchResults);
        request.TimeLimit = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 3, 60));
    }

    private static string ReadString(SearchResultEntry entry, string attributeName)
    {
        var attribute = entry.Attributes[attributeName];
        if (attribute is null || attribute.Count == 0)
        {
            return string.Empty;
        }

        return attribute[0] switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            var value => value?.ToString() ?? string.Empty
        };
    }

    private static byte[]? ReadBytes(SearchResultEntry entry, string attributeName)
    {
        var attribute = entry.Attributes[attributeName];
        if (attribute is null || attribute.Count == 0)
        {
            return null;
        }

        return attribute[0] as byte[];
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, 128));
        foreach (var character in value)
        {
            if (builder.Length >= 128)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }

    private sealed record AdIdentity(string SamAccountName, string? UserPrincipalName, string DisplayName);

    private sealed record AdUser(
        string DistinguishedName,
        string SamAccountName,
        string DisplayName,
        int UserAccountControl,
        byte[]? ObjectSid,
        int PrimaryGroupId);
}
