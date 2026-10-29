using BerTool.Shared.Contracts;
using BerTool.TrayAgent.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BerTool.TrayAgent.Reporting;

internal sealed class AgentApiClient : IDisposable
{
    private const string ApiKeyHeader = "X-BerTool-Api-Key";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly AgentSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly Uri? _baseUri;
    private bool _disposed;

    public AgentApiClient(AgentSettings settings)
    {
        _settings = settings;

        if (Uri.TryCreate(settings.ServerUrl?.TrimEnd('/'), UriKind.Absolute, out var baseUri))
        {
            _baseUri = baseUri;
        }

        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,

            // API anahtarının yönlendirme ile farklı bir sunucuya sızmasını engelle.
            AllowAutoRedirect = false
        })
        {
            Timeout = RequestTimeout
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"BerTool.TrayAgent/{typeof(AgentApiClient).Assembly.GetName().Version?.ToString(3) ?? "1.4.0"}");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<AgentConfigurationDto> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/agent/config");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var configuration = await response.Content
            .ReadFromJsonAsync<AgentConfigurationDto>(ReportingJson.Options, cancellationToken)
            .ConfigureAwait(false);

        return configuration ?? throw new InvalidOperationException("Sunucu boş yapılandırma döndürdü.");
    }

    public async Task<AgentReportAckDto> SendReportAsync(
        AgentReportDto report,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "/api/agent/report");
        request.Content = JsonContent.Create(report, options: ReportingJson.Options);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var acknowledgement = await response.Content
            .ReadFromJsonAsync<AgentReportAckDto>(ReportingJson.Options, cancellationToken)
            .ConfigureAwait(false);

        return acknowledgement ?? throw new InvalidOperationException("Sunucu boş rapor yanıtı döndürdü.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        if (!_settings.IsConfigured || _baseUri is null)
        {
            throw new InvalidOperationException(
                "Sunucu raporlaması yapılandırılmadı veya uzak sunucu adresi HTTPS değil.");
        }

        var requestUri = new Uri(
            $"{_settings.ServerUrl.TrimEnd('/')}{relativePath}",
            UriKind.Absolute);
        if (!string.Equals(requestUri.Scheme, _baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(requestUri.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            requestUri.Port != _baseUri.Port)
        {
            throw new InvalidOperationException("Sunucu adresi geçersiz.");
        }

        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, _settings.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AgentAuthenticationException(
                "Sunucu ajan API anahtarını kabul etmedi. agent-settings.json içindeki apiKey değerini kontrol edin.");
        }

        // Sunucudan gelen ham gövdeyi loglara taşıma; yalnızca kısa ve
        // temizlenmiş bir API hata mesajı kullan.
        var message = string.Empty;
        try
        {
            var payload = await response.Content
                .ReadFromJsonAsync<ApiErrorDto>(ReportingJson.Options, cancellationToken)
                .ConfigureAwait(false);
            message = SanitizeMessage(payload?.Message);
        }
        catch
        {
            message = string.Empty;
        }

        var detail = string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : $" {message}";

        throw new HttpRequestException(
            $"Sunucu isteği başarısız oldu ({(int)response.StatusCode} {response.ReasonPhrase}).{detail}".Trim());
    }

    private static string SanitizeMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 300)
        {
            trimmed = trimmed[..300];
        }

        return new string(trimmed.Where(character =>
            !char.IsControl(character) &&
            character is not ('\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E')).ToArray());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }
}

internal sealed class AgentAuthenticationException : Exception
{
    public AgentAuthenticationException(string message) : base(message)
    {
    }
}
