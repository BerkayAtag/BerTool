using BerTool.Shared.Contracts;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BerTool.Server.Configuration;

/// <summary>
/// Active Directory / LDAP kimlik doğrulama ayarları.
/// Başlangıç değerleri appsettings.json veya ortam değişkenlerinden okunur;
/// ilk kurulumdan sonra yönetim konsolundan güncellenebilir. Konsoldan
/// kaydedilen ayarlar veri dizininde şifreli hizmet hesabı parolasıyla saklanır.
/// </summary>
public sealed class AdOptions
{
    public bool Enabled { get; set; }

    /// <summary>Etki alanı denetleyicisinin DNS adı veya IP adresi.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Boş bırakılırsa LDAPS için 636, LDAP için 389 kullanılır.</summary>
    public int Port { get; set; }

    public bool UseSsl { get; set; } = true;

    public bool UseStartTls { get; set; }

    /// <summary>Yalnızca test ortamlarında kullanılmalıdır. Konsoldan değiştirilemez.</summary>
    public bool IgnoreCertificateErrors { get; set; }

    /// <summary>Şifrelenmemiş LDAP bağlantısına izin verir. Üretimde kullanılmamalıdır.</summary>
    public bool AllowInsecureLdap { get; set; }

    public string BaseDn { get; set; } = string.Empty;

    /// <summary>Kullanıcı araması ve grup sorgusu için kullanılan hizmet hesabı.</summary>
    public string BindDn { get; set; } = string.Empty;

    public string BindPassword { get; set; } = string.Empty;

    /// <summary>Yönetici yetkisi veren grup. DN veya grup adı (sAMAccountName/cn) olabilir.</summary>
    public string AdminGroup { get; set; } = string.Empty;

    /// <summary>AD kullanılamadığında yerel yönetici hesabıyla girişe izin verir.</summary>
    public bool AllowLocalFallback { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 10;

    public int EffectivePort => Port > 0 ? Port : UseSsl ? 636 : 389;

    public string EncryptionName =>
        UseSsl ? "LDAPS" : UseStartTls ? "StartTLS" : "Şifresiz LDAP";

    public bool IsTransportSecure => UseSsl || UseStartTls;

    public bool HasInsecureTransport => Enabled && !IsTransportSecure;

    public bool HasCertificateValidationDisabled => Enabled && IgnoreCertificateErrors;

    public string? Validate()
    {
        if (!Enabled)
        {
            return null;
        }

        var host = (Host ?? string.Empty).Trim().Trim('[', ']');
        if (host.Length == 0)
        {
            return "AD etkinleştirildiğinde sunucu adı (Host) zorunludur.";
        }

        if (host.Length > 255 || host.Any(char.IsWhiteSpace) || ContainsControlCharacter(host))
        {
            return "AD sunucu adı geçersiz.";
        }

        if (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            return "AD sunucu adı geçerli bir DNS adı veya IP adresi olmalıdır.";
        }

        if (Port is < 0 or > 65_535)
        {
            return "AD portu 0 ile 65535 arasında olmalıdır.";
        }

        if (UseSsl && UseStartTls)
        {
            return "LDAPS ve StartTLS aynı anda etkin olamaz.";
        }

        if (!UseSsl && !UseStartTls && !AllowInsecureLdap)
        {
            return "Şifrelenmemiş LDAP kapalıdır. LDAPS veya StartTLS etkinleştirilmeli; " +
                "yalnızca test için AllowInsecureLdap kullanılmalıdır.";
        }

        var baseDn = (BaseDn ?? string.Empty).Trim();
        if (!IsPlausibleDistinguishedName(baseDn))
        {
            return "Geçerli bir BaseDn (ör. DC=ornek,DC=local) girilmelidir.";
        }

        var bindDn = (BindDn ?? string.Empty).Trim();
        if (!IsPlausibleDistinguishedName(bindDn))
        {
            return "AD hizmet hesabı için geçerli bir BindDn zorunludur.";
        }

        if (string.IsNullOrEmpty(BindPassword))
        {
            return "AD hizmet hesabı için BindPassword zorunludur.";
        }

        if (BindPassword.Length > 512 || ContainsControlCharacter(BindPassword))
        {
            return "AD hizmet hesabı şifresi geçersiz.";
        }

        var adminGroup = (AdminGroup ?? string.Empty).Trim();
        if (adminGroup.Length == 0)
        {
            return "Yönetici grubu (AdminGroup) zorunludur.";
        }

        if (adminGroup.Length > 1024 || ContainsControlCharacter(adminGroup))
        {
            return "Yönetici grubu adı geçersiz.";
        }

        if (TimeoutSeconds is < 3 or > 60)
        {
            return "AD zaman aşımı 3 ile 60 saniye arasında olmalıdır.";
        }

        return null;
    }

    public AdStatusDto ToStatus()
    {
        var error = Validate();
        return new AdStatusDto
        {
            Enabled = Enabled,
            Configured = !Enabled || error is null,
            Host = Host,
            Port = EffectivePort,
            ConfiguredPort = Port,
            UseSsl = UseSsl,
            UseStartTls = UseStartTls,
            AllowInsecureLdap = AllowInsecureLdap,
            IgnoreCertificateErrors = IgnoreCertificateErrors,
            InsecureTransport = HasInsecureTransport,
            CertificateValidationDisabled = HasCertificateValidationDisabled,
            Encryption = EncryptionName,
            BaseDn = BaseDn,
            BindDn = BindDn,
            AdminGroup = AdminGroup,
            BindAccountConfigured = !string.IsNullOrWhiteSpace(BindDn) &&
                !string.IsNullOrEmpty(BindPassword),
            AllowLocalFallback = AllowLocalFallback,
            TimeoutSeconds = TimeoutSeconds,
            ConfigurationError = error
        };
    }

    /// <summary>
    /// Oturum çerezini AD yapılandırmasına bağlar. Yapılandırma değiştiğinde
    /// eski oturumların geçersiz olmasını sağlar. Hizmet hesabı parolası da
    /// parmak izine dahildir; parola değiştiğinde eski oturumlar düşer.
    /// </summary>
    public string CreateFingerprint()
    {
        var passwordHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(BindPassword ?? string.Empty)));

        var value = string.Join('|',
            Enabled,
            (Host ?? string.Empty).Trim().ToLowerInvariant(),
            EffectivePort,
            UseSsl,
            UseStartTls,
            IgnoreCertificateErrors,
            AllowInsecureLdap,
            (BaseDn ?? string.Empty).Trim().ToLowerInvariant(),
            (BindDn ?? string.Empty).Trim().ToLowerInvariant(),
            passwordHash,
            (AdminGroup ?? string.Empty).Trim().ToLowerInvariant(),
            AllowLocalFallback,
            TimeoutSeconds);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16];
    }

    public AdOptions Clone()
    {
        return new AdOptions
        {
            Enabled = Enabled,
            Host = Host,
            Port = Port,
            UseSsl = UseSsl,
            UseStartTls = UseStartTls,
            IgnoreCertificateErrors = IgnoreCertificateErrors,
            AllowInsecureLdap = AllowInsecureLdap,
            BaseDn = BaseDn,
            BindDn = BindDn,
            BindPassword = BindPassword,
            AdminGroup = AdminGroup,
            AllowLocalFallback = AllowLocalFallback,
            TimeoutSeconds = TimeoutSeconds
        };
    }

    private static bool IsPlausibleDistinguishedName(string value)
    {
        if (value.Length is < 3 or > 1024 || ContainsControlCharacter(value))
        {
            return false;
        }

        var firstEquals = value.IndexOf('=');
        if (firstEquals <= 0 || firstEquals == value.Length - 1)
        {
            return false;
        }

        return value.Contains(',', StringComparison.Ordinal) || value.Count(character => character == '=') == 1;
    }

    private static bool ContainsControlCharacter(string value)
    {
        return value.Any(character => char.IsControl(character));
    }
}
