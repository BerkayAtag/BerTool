using BerTool.Server.Configuration;
using BerTool.Server.Security;
using BerTool.Server.Storage;
using BerTool.Shared.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace BerTool.Server.Endpoints;

internal static class AdminEndpoints
{
    private const string SessionVersionClaim = "bertool:sv";
    private const string AuthenticationMethodClaim = "bertool:auth";
    private const string DisplayNameClaim = "bertool:display";
    private const string AdFingerprintClaim = "bertool:adfp";

    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/login", LoginAsync).RequireRateLimiting("login");

        app.MapGet("/api/admin/session", async (
            HttpContext context,
            ServerStore store,
            AdAuthenticationService adService,
            CancellationToken cancellationToken) =>
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                var status = store.GetAdStatus();
                return Results.Ok(new AdminSessionDto
                {
                    Authenticated = false,
                    AdEnabled = status.Enabled,
                    AdConfigured = status.Configured,
                    LocalLoginEnabled = !status.Enabled || status.AllowLocalFallback,
                    ServerTimeUtc = DateTimeOffset.UtcNow
                });
            }

            var settings = await store.GetAdminSettingsAsync(cancellationToken);
            return Results.Ok(CreateAuthenticatedSession(context, settings, adService));
        });

        var group = app.MapGroup("/api/admin").RequireAuthorization();

        group.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new ApiMessageDto { Message = "Oturum kapatıldı." });
        });

        group.MapGet("/dashboard", async (
            string? status,
            string? search,
            string? product,
            string? productState,
            int? page,
            int? pageSize,
            ServerStore store,
            CancellationToken cancellationToken) =>
        {
            var validation = ValidateDashboardQuery(status, search, productState);
            if (validation is not null)
            {
                return Results.BadRequest(new ApiErrorDto { Message = validation });
            }

            var dashboard = await store.GetDashboardAsync(
                status,
                search,
                product,
                productState,
                page ?? 1,
                pageSize ?? 50,
                cancellationToken);

            return Results.Ok(dashboard);
        });

        group.MapGet("/machines/{machineId}", async (
            string machineId,
            ServerStore store,
            CancellationToken cancellationToken) =>
        {
            var detail = await store.GetMachineDetailAsync(machineId, cancellationToken);
            return detail is null
                ? Results.NotFound(new ApiErrorDto { Message = "Makine kaydı bulunamadı." })
                : Results.Ok(detail);
        });

        group.MapGet("/export", async (
            string? status,
            string? search,
            string? product,
            string? productState,
            ServerStore store,
            CancellationToken cancellationToken) =>
        {
            var validation = ValidateDashboardQuery(status, search, productState);
            if (validation is not null)
            {
                return Results.BadRequest(new ApiErrorDto { Message = validation });
            }

            var machines = await store.GetFilteredMachinesForExportAsync(
                status,
                search,
                product,
                productState,
                cancellationToken);

            var csv = BuildCsv(machines);
            var preamble = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble();
            var bytes = preamble.Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
            var fileName = $"bertool-makineler-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
            return Results.File(bytes, "text/csv; charset=utf-8", fileName);
        });

        group.MapGet("/products", async (ServerStore store, CancellationToken cancellationToken) =>
        {
            var products = await store.GetProductsAsync(cancellationToken);
            return Results.Ok(products);
        });

        group.MapPost("/products", async (
            ProductUpsertRequest request,
            ServerStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var product = await store.CreateProductAsync(request, cancellationToken);
                return Results.Ok(product);
            }
            catch (StoreValidationException exception)
            {
                return Results.BadRequest(new ApiErrorDto { Message = exception.Message });
            }
        });

        group.MapPut("/products/{productId}", async (
            string productId,
            ProductUpsertRequest request,
            ServerStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var product = await store.UpdateProductAsync(productId, request, cancellationToken);
                return Results.Ok(product);
            }
            catch (StoreValidationException exception)
            {
                return Results.BadRequest(new ApiErrorDto { Message = exception.Message });
            }
            catch (StoreNotFoundException exception)
            {
                return Results.NotFound(new ApiErrorDto { Message = exception.Message });
            }
        });

        group.MapDelete("/products/{productId}", async (
            string productId,
            ServerStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await store.DeleteProductAsync(productId, cancellationToken);
                return Results.Ok(new ApiMessageDto { Message = "Ürün tanımı silindi." });
            }
            catch (StoreNotFoundException exception)
            {
                return Results.NotFound(new ApiErrorDto { Message = exception.Message });
            }
        });

        group.MapGet("/settings", async (
            ServerStore store,
            CancellationToken cancellationToken) =>
        {
            var settings = await store.GetAdminSettingsAsync(cancellationToken);
            settings.Ad = store.GetAdStatus();
            return Results.Ok(settings);
        });

        group.MapPut("/settings", async (
            UpdateAdminSettingsRequest request,
            ServerStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var settings = await store.UpdateSettingsAsync(request, cancellationToken);
                settings.Ad = store.GetAdStatus();
                return Results.Ok(settings);
            }
            catch (StoreValidationException exception)
            {
                return Results.BadRequest(new ApiErrorDto { Message = exception.Message });
            }
        });

        group.MapPut("/ad", async (
            AdSettingsUpdateRequest request,
            ServerStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await store.UpdateAdSettingsAsync(request, cancellationToken);
                return Results.Ok(store.GetAdStatus());
            }
            catch (StoreValidationException exception)
            {
                return Results.BadRequest(new ApiErrorDto { Message = exception.Message });
            }
        });

        group.MapPost("/ad/test", async (
            HttpContext context,
            ServerStore store,
            AdAuthenticationService adService,
            CancellationToken cancellationToken) =>
        {
            AdSettingsUpdateRequest? request = null;

            // Gövde isteğe bağlıdır: eski istemciler kayıtlı ayarları test
            // ederken, konsol kaydedilmemiş form taslağını gönderebilir.
            if (context.Request.ContentLength is > 0 ||
                context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                try
                {
                    request = await context.Request.ReadFromJsonAsync<AdSettingsUpdateRequest>(
                        cancellationToken);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new ApiErrorDto
                    {
                        Message = "AD test isteği geçerli JSON biçiminde değil."
                    });
                }
            }

            var draft = request is null
                ? store.GetAdOptions()
                : store.CreateAdDraft(request);

            var validationError = draft.Validate();
            if (validationError is not null)
            {
                return Results.Ok(new AdConnectionTestResultDto
                {
                    Success = false,
                    Message = validationError
                });
            }

            var result = await Task.Run(
                () => adService.TestConnection(draft),
                cancellationToken);
            return Results.Ok(result);
        }).RequireRateLimiting("ad-test");

        group.MapPost("/agent-key/rotate", async (ServerStore store, CancellationToken cancellationToken) =>
        {
            var apiKey = await store.RotateAgentApiKeyAsync(cancellationToken);
            return Results.Ok(new RotateAgentKeyResponse { ApiKey = apiKey });
        });

        group.MapPost("/password", async (
            ChangePasswordRequest request,
            HttpContext context,
            ServerStore store,
            CancellationToken cancellationToken) =>
        {
            if (string.Equals(
                    context.User.FindFirstValue(AuthenticationMethodClaim),
                    "ad",
                    StringComparison.Ordinal))
            {
                return Results.BadRequest(new ApiErrorDto
                {
                    Message = "Active Directory ile giriş yapan kullanıcılar yerel yönetici şifresini değiştiremez."
                });
            }

            try
            {
                await store.ChangePasswordAsync(
                    request.CurrentPassword,
                    request.NewPassword,
                    cancellationToken);
            }
            catch (StoreValidationException exception)
            {
                return Results.BadRequest(new ApiErrorDto { Message = exception.Message });
            }

            var settings = await store.GetAdminSettingsAsync(cancellationToken);
            await SignInAdminAsync(
                context,
                settings.AdminUsername,
                settings.AdminUsername,
                "local",
                store.GetSessionVersion(),
                adFingerprint: string.Empty);
            return Results.Ok(new ApiMessageDto { Message = "Yönetici şifresi güncellendi." });
        });
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext context,
        ServerStore store,
        AdAuthenticationService adService,
        LoginThrottle throttle,
        ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            request.Username.Length > 256 ||
            request.Password.Length > 512)
        {
            return Results.BadRequest(new ApiErrorDto { Message = "Kullanıcı adı ve şifre zorunludur." });
        }

        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        if (throttle.IsBlocked(remoteIp, request.Username, out var retryAfter))
        {
            context.Response.Headers.RetryAfter =
                Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

            return Results.Json(
                new ApiErrorDto
                {
                    Message = "Çok fazla başarısız giriş denemesi yapıldı. Lütfen birkaç dakika sonra tekrar deneyin."
                },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var adResult = adService.Authenticate(request.Username, request.Password);
        if (adResult.Status == AdAuthenticationStatus.Success)
        {
            throttle.Reset(remoteIp, request.Username);
            var settings = await store.GetAdminSettingsAsync(context.RequestAborted);
            await SignInAdminAsync(
                context,
                adResult.Username,
                adResult.DisplayName,
                "ad",
                store.GetSessionVersion(),
                adService.Fingerprint);

            logger.LogInformation(
                "Yönetici girişi başarılı (Active Directory). Kullanıcı: {Username}. Uzak IP: {RemoteIp}",
                adResult.Username,
                remoteIp);

            return Results.Ok(CreateAuthenticatedSession(context, settings, adService));
        }

        // Yerel yönetici hesabı, AD devre dışıyken veya AD erişilemezken
        // kurtarma (break-glass) yolu olarak kullanılabilir.
        if (adService.IsLocalLoginEnabled &&
            store.VerifyAdminCredentials(request.Username, request.Password))
        {
            throttle.Reset(remoteIp, request.Username);
            var settings = await store.GetAdminSettingsAsync(context.RequestAborted);
            await SignInAdminAsync(
                context,
                settings.AdminUsername,
                settings.AdminUsername,
                "local",
                store.GetSessionVersion(),
                adFingerprint: string.Empty);

            logger.LogInformation(
                "Yönetici girişi başarılı (yerel hesap). Uzak IP: {RemoteIp}",
                remoteIp);

            return Results.Ok(CreateAuthenticatedSession(context, settings, adService));
        }

        // Kaba kuvvet ve kullanıcı adı keşif denemelerini yavaşlatmak için sabit gecikme.
        try
        {
            await Task.Delay(350, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
        }

        if (adResult.Status == AdAuthenticationStatus.NotAuthorized)
        {
            throttle.RecordFailure(remoteIp, request.Username);
            logger.LogWarning(
                "AD kullanıcısı yönetici grubunda olmadığı için reddedildi. Kullanıcı: {Username}. Uzak IP: {RemoteIp}",
                adResult.Username,
                remoteIp);

            return Results.Json(
                new ApiErrorDto
                {
                    Message = "Bu hesabın yönetim konsoluna erişim yetkisi yok."
                },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (adResult.Status is AdAuthenticationStatus.Unavailable or AdAuthenticationStatus.ConfigurationError)
        {
            logger.LogError(
                "AD kimlik doğrulaması kullanılamıyor. Uzak IP: {RemoteIp}. Ayrıntı: {Error}",
                remoteIp,
                adResult.Error);

            return Results.Json(
                new ApiErrorDto
                {
                    Message = adService.IsLocalLoginEnabled
                        ? "Active Directory kimlik doğrulaması şu anda kullanılamıyor. Yerel yönetici hesabıyla giriş yapmayı deneyin."
                        : "Active Directory kimlik doğrulaması şu anda kullanılamıyor."
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        throttle.RecordFailure(remoteIp, request.Username);
        logger.LogWarning(
            "Başarısız yönetici giriş denemesi. Uzak IP: {RemoteIp}",
            remoteIp);

        return Results.Json(
            new ApiErrorDto { Message = "Kullanıcı adı veya şifre hatalı." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    private static AdminSessionDto CreateAuthenticatedSession(
        HttpContext context,
        AdminSettingsDto settings,
        AdAuthenticationService adService)
    {
        var username = context.User.Identity?.Name ?? settings.AdminUsername;
        var displayName = context.User.FindFirstValue(DisplayNameClaim);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = username;
        }

        var adStatus = settings.Ad ?? adService.GetStatus();

        return new AdminSessionDto
        {
            Authenticated = true,
            Username = username,
            DisplayName = displayName,
            AuthenticationMethod = context.User.FindFirstValue(AuthenticationMethodClaim) ?? "local",
            AdEnabled = adStatus.Enabled,
            AdConfigured = adStatus.Configured,
            LocalLoginEnabled = !adStatus.Enabled || adStatus.AllowLocalFallback,
            ConfigVersion = settings.ConfigVersion,
            ServerTimeUtc = DateTimeOffset.UtcNow
        };
    }

    private static async Task SignInAdminAsync(
        HttpContext context,
        string username,
        string displayName,
        string authenticationMethod,
        int sessionVersion,
        string adFingerprint)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, "Admin"),
            new(SessionVersionClaim, sessionVersion.ToString(CultureInfo.InvariantCulture)),
            new(AuthenticationMethodClaim, authenticationMethod),
            new(DisplayNameClaim, displayName),
            new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
        };

        if (!string.IsNullOrWhiteSpace(adFingerprint))
        {
            claims.Add(new Claim(AdFingerprintClaim, adFingerprint));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = false,
                IssuedUtc = DateTimeOffset.UtcNow
            });
    }

    private static string? ValidateDashboardQuery(string? status, string? search, string? productState)
    {
        if (!string.IsNullOrWhiteSpace(status) &&
            !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "healthy", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "risky", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "offline", StringComparison.OrdinalIgnoreCase))
        {
            return "Geçersiz durum filtresi.";
        }

        if (!string.IsNullOrWhiteSpace(productState) &&
            !string.Equals(productState, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(productState, "healthy", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(productState, "unhealthy", StringComparison.OrdinalIgnoreCase))
        {
            return "Geçersiz ürün durumu filtresi.";
        }

        if (search is { Length: > 200 })
        {
            return "Arama metni en fazla 200 karakter olabilir.";
        }

        return null;
    }

    private static string BuildCsv(IReadOnlyList<MachineSummaryDto> machines)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(';',
            Csv("Durum"),
            Csv("Makine"),
            Csv("Domain"),
            Csv("Kullanıcı"),
            Csv("İşletim Sistemi"),
            Csv("Model"),
            Csv("IP Adresleri"),
            Csv("Ürün Durumları"),
            Csv("Risk Nedenleri"),
            Csv("Son Görülme"),
            Csv("Son Rapor"),
            Csv("Ajan Sürümü"),
            Csv("Yapılandırma Güncel")));

        foreach (var machine in machines)
        {
            var productSummary = string.Join(" | ", machine.ProductStatuses.Select(status =>
                $"{status.DisplayName}: {status.StatusText}"));

            builder.AppendLine(string.Join(';',
                Csv(machine.StatusText),
                Csv(machine.MachineName),
                Csv(machine.DomainOrWorkgroup),
                Csv(machine.LoggedInUser),
                Csv(machine.OperatingSystem),
                Csv(machine.ComputerModel),
                Csv(string.Join(", ", machine.IpAddresses)),
                Csv(productSummary),
                Csv(string.Join(" | ", machine.RiskReasons)),
                Csv(machine.LastSeenUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)),
                Csv(machine.CollectedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)),
                Csv(machine.AgentVersion),
                Csv(machine.IsConfigCurrent ? "Evet" : "Hayır")));
        }

        return builder.ToString();
    }

    private static string Csv(string? value) => CsvFormatter.Escape(value);
}
