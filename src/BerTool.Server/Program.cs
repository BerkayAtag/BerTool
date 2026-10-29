using BerTool.Server.Configuration;
using BerTool.Server.Endpoints;
using BerTool.Server.Security;
using BerTool.Server.Storage;
using BerTool.Shared.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
    options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
});

var serverOptions = builder.Configuration
    .GetSection(ServerOptions.SectionName)
    .Get<ServerOptions>() ?? new ServerOptions();

serverOptions.Ad ??= new AdOptions();
serverOptions.AllowedHosts ??= Array.Empty<string>();
serverOptions.AllowedOrigins ??= Array.Empty<string>();
serverOptions.TrustedProxies ??= Array.Empty<string>();

var dataDirectory = Path.IsPathRooted(serverOptions.DataDirectory)
    ? serverOptions.DataDirectory
    : Path.Combine(builder.Environment.ContentRootPath, serverOptions.DataDirectory);

Directory.CreateDirectory(dataDirectory);

// Ters proxy arkasında TLS sonlandırılıyorsa, çerezlerin Secure bayrağı ve
// Origin doğrulaması için gerçek şema/host bilgisi gerekir. Yalnızca açıkça
// güvenilen proxy IP'leri bu bilgiyi değiştirebilir.
var trustedProxies = (serverOptions.TrustedProxies ?? Array.Empty<string>())
    .Select(value => IPAddress.TryParse(value?.Trim(), out var address) ? address : null)
    .Where(address => address is not null)
    .Cast<IPAddress>()
    .ToArray();

if (trustedProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto |
            ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in trustedProxies)
        {
            options.KnownProxies.Add(proxy);
        }
    });
}

builder.Services.AddSingleton(serverOptions);

var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("BerTool.Server");

// Anahtar dosyaları (oturum çerezleri ve AD hizmet hesabı parolası bunlarla
// korunur) diskte düz metin kalmasın. Sunucu kullanıcı bazlı kurulduğu için
// DPAPI kullanıcı kapsamı yeterlidir; eski şifresiz anahtarlar okunmaya devam eder.
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: false);
}

builder.Services.AddSingleton(serviceProvider => new ServerStore(
    serverOptions,
    dataDirectory,
    serviceProvider.GetRequiredService<ILogger<ServerStore>>(),
    serviceProvider.GetRequiredService<IDataProtectionProvider>()));

builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<AdAuthenticationService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "bertool.admin.sid";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.Path = "/";
        options.LoginPath = "/";
        options.AccessDeniedPath = "/";
        options.ExpireTimeSpan = TimeSpan.FromHours(Math.Clamp(serverOptions.SessionHours, 1, 72));
        options.SlidingExpiration = false;

        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return context.Response.WriteAsJsonAsync(
                new ApiErrorDto { Message = "Yönetici oturumu gerekli." });
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(
                new ApiErrorDto { Message = "Bu işlem için yetkiniz yok." });
        };

        options.Events.OnValidatePrincipal = async context =>
        {
            var store = context.HttpContext.RequestServices.GetRequiredService<ServerStore>();
            var adService = context.HttpContext.RequestServices.GetRequiredService<AdAuthenticationService>();
            var versionClaim = context.Principal?.FindFirstValue("bertool:sv");

            var sessionIsCurrent =
                int.TryParse(versionClaim, out var sessionVersion) &&
                sessionVersion == store.GetSessionVersion();
            var authenticationIsCurrent = adService.IsSessionAllowed(
                context.Principal?.FindFirstValue("bertool:auth"),
                context.Principal?.FindFirstValue("bertool:adfp"));

            if (!sessionIsCurrent || !authenticationIsCurrent)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy("ad-test", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 8,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.OnRejected = async (rejectedContext, cancellationToken) =>
    {
        rejectedContext.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        rejectedContext.HttpContext.Response.Headers.RetryAfter = "60";

        var isAdTest = rejectedContext.HttpContext.Request.Path
            .StartsWithSegments("/api/admin/ad/test", StringComparison.OrdinalIgnoreCase);
        await rejectedContext.HttpContext.Response.WriteAsJsonAsync(
            new ApiErrorDto
            {
                Message = isAdTest
                    ? "Çok fazla Active Directory bağlantı testi yapıldı. Lütfen bir dakika sonra tekrar deneyin."
                    : "Çok fazla giriş denemesi yapıldı. Lütfen bir dakika sonra tekrar deneyin."
            },
            cancellationToken);
    };
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();

if (trustedProxies.Length > 0)
{
    app.UseForwardedHeaders();
}

var store = app.Services.GetRequiredService<ServerStore>();
await store.InitializeAsync();

var adStatus = store.GetAdStatus();
if (adStatus.Enabled)
{
    app.Logger.LogInformation(
        "Active Directory entegrasyonu etkin. Sunucu: {Host}:{Port}, şifreleme: {Encryption}, grup: {Group}, yerel yedek giriş: {LocalFallback}",
        adStatus.Host,
        adStatus.Port,
        adStatus.Encryption,
        adStatus.AdminGroup,
        adStatus.AllowLocalFallback ? "açık" : "kapalı");

    if (adStatus.InsecureTransport)
    {
        app.Logger.LogWarning(
            "AD bağlantısı şifrelenmemiş LDAP kullanıyor. Bu yapılandırma yalnızca test ortamları için uygundur.");
    }

    if (adStatus.CertificateValidationDisabled)
    {
        app.Logger.LogWarning(
            "AD sertifika doğrulaması devre dışı. Bu yapılandırma yalnızca test ortamları için uygundur.");
    }
}
else
{
    app.Logger.LogInformation("Active Directory entegrasyonu kapalı; yerel yönetici girişi kullanılıyor.");
}

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(feature?.Error, "İşlenmeyen sunucu hatası. Yol: {Path}", context.Request.Path);

        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new ApiErrorDto
        {
            Message = "Sunucuda beklenmeyen bir hata oluştu. Ayrıntılar sunucu loglarına yazıldı."
        });
    });
});

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    headers["Cross-Origin-Resource-Policy"] = "same-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; " +
        "object-src 'none'";

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        headers["Cache-Control"] = "no-store, no-cache";
        headers["Pragma"] = "no-cache";
    }

    await next();
});

app.UseHsts();

// Uzak istemcilerde HTTP'yi reddet, Host başlığını doğrula ve durum
// değiştiren yönetici isteklerinde aynı kaynak kanıtı ara.
app.UseMiddleware<RemoteHttpsMiddleware>();
app.UseMiddleware<HostValidationMiddleware>();
app.UseMiddleware<OriginValidationMiddleware>();

app.UseRateLimiter();
app.UseAgentApiKeyGate();
app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // Konsol güncellemelerinden sonra tarayıcının eski index.html/app.js
        // dosyalarını kullanmasını engelle. Küçük yerel arayüz dosyaları için
        // her istekte yeniden doğrulama maliyeti önemsizdir.
        var headers = context.Context.Response.Headers;
        headers.CacheControl = "no-cache, no-store, must-revalidate";
        headers.Pragma = "no-cache";
        headers.Expires = "0";
    }
});

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    service = "BerTool.Server",
    serverTimeUtc = DateTimeOffset.UtcNow
}));

app.MapAgentEndpoints();
app.MapAdminEndpoints();

app.Logger.LogInformation(
    "BerTool sunucusu başlatılıyor. Veri dizini: {DataDirectory}. Arayüz: {Urls}",
    dataDirectory,
    builder.Configuration["Urls"] ?? "http://localhost:5080");

app.Run();
