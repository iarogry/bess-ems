using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BatteryEms.Adapters.Optimization.Deye;

public sealed record DeyeCloudAuthOptions(
    Uri BaseUri,
    string AppId,
    string AppSecret,
    string Email,
    string Password,
    long? CompanyId = null);

/// <summary>Authenticates against Deye Cloud and adds a short-lived bearer token.</summary>
public sealed class DeyeCloudAuthenticationHandler : DelegatingHandler
{
    private readonly HttpClient _authClient;
    private readonly DeyeCloudAuthOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public DeyeCloudAuthenticationHandler(HttpClient authClient, DeyeCloudAuthOptions options)
    {
        _authClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        await InvalidateAsync().ConfigureAwait(false);
        token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _token;
            var body = new Dictionary<string, object?>
            {
                ["appSecret"] = _options.AppSecret,
                ["email"] = _options.Email,
                ["password"] = Sha256Lower(_options.Password)
            };
            if (_options.CompanyId is not null) body["companyId"] = _options.CompanyId.Value;
            using var content = JsonContent.Create(body);
            using var response = await _authClient.PostAsync(new Uri(_options.BaseUri, $"account/token?appId={Uri.EscapeDataString(_options.AppId)}"), content, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var token = FindString(root, "accessToken") ?? throw new InvalidOperationException("Deye token response did not contain accessToken.");
            _token = token;
            var expiresIn = FindInt(root, "expiresIn") ?? 3600;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));
            return token;
        }
        finally { _gate.Release(); }
    }

    private async Task InvalidateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _token = null; _expiresAt = default; }
        finally { _gate.Release(); }
    }

    private static string Sha256Lower(string value)
    {
        if (value.Length == 64 && value.All(Uri.IsHexDigit)) return Convert.ToHexStringLower(Convert.FromHexString(value));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string? FindString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out value)) return value.GetString();
        return null;
    }

    private static int? FindInt(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.TryGetInt32(out var direct)) return direct;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out value) && value.TryGetInt32(out var nested)) return nested;
        return null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _gate.Dispose();
        base.Dispose(disposing);
    }
}
