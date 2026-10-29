using BerTool.Server.Configuration;
using BerTool.Shared.Contracts;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BerTool.Server.Security;

/// <summary>
/// Uzak istemciler için HTTPS zorunluluğu. Loopback bağlantıları ve sağlık
/// kontrolü istisnadır; ters proxy arkasında TLS sonlandırılıyorsa
/// ForwardedHeaders ile gerçek şema okunur.
/// </summary>
internal sealed class RemoteHttpsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ServerOptions _options;

    public RemoteHttpsMiddleware(RequestDelegate next, ServerOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_options.RequireHttpsForRemote &&
            !context.Request.IsHttps &&
            !IsLoopback(context.Connection.RemoteIpAddress) &&
            !context.Request.Path.StartsWithSegments("/healthz"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new ApiErrorDto
            {
                Message = "Uzak bağlantılar için HTTPS zorunludur. Sunucuyu HTTPS ile yapılandırın veya güvenilir bir ters proxy kullanın."
            });
            return;
        }

        await _next(context);
    }

    private static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address);
    }
}

/// <summary>
/// Host başlığı doğrulaması. Varsayılan olarak loopback, makine adı ve yerel
/// IP adresleri kabul edilir; genel DNS adı veya ters proxy kullanılıyorsa
/// BerTool:AllowedHosts listesine eklenmelidir.
/// </summary>
internal sealed class HostValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<HostValidationMiddleware> _logger;
    private readonly HashSet<string> _allowedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _allowedSuffixes = new();
    private readonly bool _allowAny;

    public HostValidationMiddleware(
        RequestDelegate next,
        ServerOptions options,
        ILogger<HostValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        var configured = (options.AllowedHosts ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToList();

        _allowAny = configured.Any(value => value == "*");
        if (_allowAny)
        {
            _logger.LogWarning(
                "Host doğrulaması tüm ana bilgisayar adlarına açık. Üretimde BerTool:AllowedHosts listesini sınırlayın.");
        }

        AddAllowedHost("localhost");
        AddAllowedHost("127.0.0.1");
        AddAllowedHost("::1");
        AddAllowedHost(Environment.MachineName);
        AddAllowedHost(Dns.GetHostName());

        foreach (var address in GetLocalAddresses())
        {
            AddAllowedHost(address);
        }

        foreach (var value in configured)
        {
            if (value.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = value[1..].TrimEnd('.');
                if (suffix.Length > 1)
                {
                    _allowedSuffixes.Add(suffix);
                }

                continue;
            }

            AddAllowedHost(value);
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_allowAny)
        {
            await _next(context);
            return;
        }

        var rawHost = context.Request.Host.Host;
        var host = NormalizeHost(rawHost);
        if (host.Length == 0 ||
            host.Length > 255 ||
            host.Any(character =>
                char.IsControl(character) ||
                char.IsWhiteSpace(character) ||
                character is '/' or '\\' or '@' or '#' or '?'))
        {
            await RejectAsync(context);
            return;
        }

        if (_allowedHosts.Contains(host) ||
            _allowedSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        _logger.LogDebug("Geçersiz Host başlığı reddedildi: {Host}", host);
        await RejectAsync(context);
    }

    private void AddAllowedHost(string? value)
    {
        var host = NormalizeHost(value);
        if (host.Length > 0)
        {
            _allowedHosts.Add(host);
        }
    }

    private static string NormalizeHost(string? value)
    {
        var host = (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

        // IPv6 adresleri köşeli parantezli veya parantezsiz gelebilir.
        host = host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var address))
        {
            return address.ToString().ToLowerInvariant();
        }

        return host;
    }

    private static IEnumerable<string> GetLocalAddresses()
    {
        var addresses = new List<string>();

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    {
                        addresses.Add(unicast.Address.ToString());
                    }
                }
            }
        }
        catch
        {
            // Ağ arayüzleri okunamazsa yalnızca loopback ve makine adı kullanılır.
        }

        return addresses;
    }

    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new ApiErrorDto
        {
            Message = "Geçersiz ana bilgisayar adı. Sunucu adresini kontrol edin veya yöneticinize başvurun."
        });
    }
}

/// <summary>
/// Durum değiştiren yönetici isteklerinde aynı kaynak kanıtı arar. Tarayıcı
/// kaynaklı isteklerde Origin veya Sec-Fetch-Site başlığı bulunur; ikisi de
/// yoksa istek reddedilir. Bu, SameSite=Strict çerez korumasına ek bir
/// savunma katmanıdır.
/// </summary>
internal sealed class OriginValidationMiddleware
{
    private static readonly string[] SameOriginFetchSites = { "same-origin", "none" };

    private readonly RequestDelegate _next;
    private readonly ServerOptions _options;

    public OriginValidationMiddleware(RequestDelegate next, ServerOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsStateChangingMethod(context.Request.Method) &&
            context.Request.Path.StartsWithSegments("/api/admin"))
        {
            var originValues = context.Request.Headers.Origin;
            var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString().Trim();

            if (originValues.Count > 0)
            {
                if (originValues.Count != 1 || !IsAllowedOrigin(context, originValues[0]))
                {
                    await RejectAsync(context);
                    return;
                }
            }
            else if (!SameOriginFetchSites.Contains(fetchSite, StringComparer.OrdinalIgnoreCase))
            {
                await RejectAsync(context);
                return;
            }
        }

        await _next(context);
    }

    private bool IsAllowedOrigin(HttpContext context, string? originValue)
    {
        var origin = (originValue ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(origin) || string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}".TrimEnd('/');
        if (string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (_options.AllowedOrigins ?? Array.Empty<string>()).Any(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            string.Equals(candidate.Trim().TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new ApiErrorDto
        {
            Message = "İstek kaynağı doğrulanamadı."
        });
    }

    private static bool IsStateChangingMethod(string method)
    {
        return HttpMethods.IsPost(method) ||
            HttpMethods.IsPut(method) ||
            HttpMethods.IsPatch(method) ||
            HttpMethods.IsDelete(method);
    }
}
