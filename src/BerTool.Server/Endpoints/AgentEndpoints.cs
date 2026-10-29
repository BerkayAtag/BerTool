using BerTool.Server.Storage;
using BerTool.Shared.Contracts;

namespace BerTool.Server.Endpoints;

internal static class AgentEndpoints
{
    private const string ApiKeyHeader = "X-BerTool-Api-Key";
    private const int MaxApiKeyLength = 256;

    public static void MapAgentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/agent");

        group.MapGet("/config", async (
            HttpContext context,
            ServerStore store,
            CancellationToken cancellationToken) =>
        {
            if (!IsAuthorized(context, store))
            {
                return Unauthorized();
            }

            var configuration = await store.GetAgentConfigurationAsync(cancellationToken);
            return Results.Ok(configuration);
        });

        group.MapPost("/report", async (
            HttpContext context,
            AgentReportDto report,
            ServerStore store,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            if (!IsAuthorized(context, store))
            {
                logger.LogWarning(
                    "Geçersiz ajan API anahtarıyla rapor denemesi. Uzak IP: {RemoteIp}",
                    context.Connection.RemoteIpAddress);
                return Unauthorized();
            }

            AgentReportDto normalized;
            try
            {
                normalized = ReportSanitizer.Normalize(report);
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new ApiErrorDto { Message = exception.Message });
            }

            try
            {
                var acknowledgement = await store.SaveReportAsync(normalized, cancellationToken);
                return Results.Ok(acknowledgement);
            }
            catch (StoreValidationException exception)
            {
                return Results.BadRequest(new ApiErrorDto { Message = exception.Message });
            }
        });

        group.MapPost("/heartbeat", async (
            HttpContext context,
            ServerStore store,
            CancellationToken cancellationToken) =>
        {
            if (!IsAuthorized(context, store))
            {
                return Unauthorized();
            }

            var configuration = await store.GetAgentConfigurationAsync(cancellationToken);
            return Results.Ok(configuration);
        });
    }

    /// <summary>
    /// API anahtarını istek gövdesi okunmadan doğrular. Minimal API rapor
    /// gövdesini (2 MB'a kadar) işleyiciden önce ayrıştırdığı için, anahtarsız
    /// istemcilerin sunucuya JSON ayrıştırma yükü bindirmesini engeller.
    /// </summary>
    public static void UseAgentApiKeyGate(this WebApplication app)
    {
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/api/agent"),
            branch => branch.Use(async (context, next) =>
            {
                var store = context.RequestServices.GetRequiredService<ServerStore>();
                if (!IsAuthorized(context, store))
                {
                    context.RequestServices.GetRequiredService<ILogger<Program>>().LogWarning(
                        "Geçersiz ajan API anahtarıyla istek reddedildi. Yol: {Path}. Uzak IP: {RemoteIp}",
                        context.Request.Path,
                        context.Connection.RemoteIpAddress);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(
                        new ApiErrorDto { Message = "Geçersiz veya eksik ajan API anahtarı." });
                    return;
                }

                await next(context);
            }));
    }

    private static bool IsAuthorized(HttpContext context, ServerStore store)
    {
        var values = context.Request.Headers[ApiKeyHeader];
        if (values.Count != 1)
        {
            return false;
        }

        var apiKey = values[0];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > MaxApiKeyLength)
        {
            return false;
        }

        return store.ValidateAgentApiKey(apiKey);
    }

    private static IResult Unauthorized()
    {
        return Results.Json(
            new ApiErrorDto { Message = "Geçersiz veya eksik ajan API anahtarı." },
            statusCode: StatusCodes.Status401Unauthorized);
    }
}
