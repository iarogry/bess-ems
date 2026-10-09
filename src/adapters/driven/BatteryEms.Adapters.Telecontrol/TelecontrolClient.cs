using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using BatteryEms.Application.Realtime;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.Telecontrol;

public sealed class TelecontrolClient : IDisposable
{
    private readonly IHttpClientFactory _factory;
    private readonly TelecontrolOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;
    private DateTimeOffset _nextLoginAttempt;

    public TelecontrolClient(IHttpClientFactory factory, IOptions<TelecontrolOptions> options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        _factory = factory;
        _options = options.Value;
    }

    public async Task<ChpTelemetry> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = _factory.CreateClient("Telecontrol");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (_token is null || now >= _expiresAt)
                {
                    await LoginAsync(client, now, cancellationToken).ConfigureAwait(false);
                }
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    new Uri(_options.GatewayBaseUrl, "api?call=GetLastDatapoints"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                // Use a buffered JSON body, matching the installed desktop client's request format.
                request.Content = new StringContent(JsonSerializer.Serialize(new { deviceId = _options.DeviceId }), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    _token = null;
                    continue;
                }
                response.EnsureSuccessStatusCode();
                using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException("Telecontrol response is empty.");
                return TelecontrolPayload.Parse(body.RootElement, _options, now);
            }
            throw new HttpRequestException("Telecontrol authorization failed.");
        }
        finally { _gate.Release(); }
    }

    private async Task LoginAsync(HttpClient client, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now < _nextLoginAttempt) { throw new HttpRequestException("Telecontrol login retry cooldown."); }
        _nextLoginAttempt = now.AddMinutes(5);
        using var response = await client.PostAsJsonAsync(
            new Uri(_options.AuthBaseUrl, "auth/token"),
            new { userName = _options.Username, password = _options.Password, appType = "wpf",
                appVersion = "1.5.0.0", deploymentVersion = "1.5.0.0" }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Telecontrol authentication response is empty.");
        var root = body.RootElement;
        var token = root.TryGetProperty("token", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(token)) { throw new JsonException("Telecontrol token is absent."); }
        _token = token;
        // A short cache also supports older responses without a parseable expiry.
        _expiresAt = now.AddMinutes(1);
        if (root.TryGetProperty("expiration", out var expiration) && expiration.ValueKind == JsonValueKind.String && expiration.TryGetDateTimeOffset(out var parsed))
        {
            _expiresAt = parsed.AddSeconds(-30);
        }
        _nextLoginAttempt = now;
    }

    public void Dispose() => _gate.Dispose();
}
