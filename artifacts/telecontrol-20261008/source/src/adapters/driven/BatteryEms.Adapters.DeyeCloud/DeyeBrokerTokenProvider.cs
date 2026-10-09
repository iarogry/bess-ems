using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatteryEms.Application.Time;

namespace BatteryEms.Adapters.DeyeCloud;

// Deliberately not a record: generated ToString must not expose credentials.
public sealed class DeyeBrokerTokenOptions
{
    public required string AppId { get; init; }
    public required string AppSecret { get; init; }
    public required string Email { get; init; }
    public required string Password { get; init; }
    public long? CompanyId { get; init; }

    public DeyeBrokerTokenOptions EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AppSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(Email);
        ArgumentException.ThrowIfNullOrWhiteSpace(Password);
        if (CompanyId is <= 0) { throw new ArgumentOutOfRangeException(nameof(CompanyId)); }
        return this;
    }
}

public sealed class DeyeBrokerAccessToken
{
    internal DeyeBrokerAccessToken(string value, DateTimeOffset expiresAtUtc)
    {
        Value = value;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string Value { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public override string ToString() => "Deye broker access token (redacted)";
}

// Acquire BEFORE durable attempt admission. This is not a DelegatingHandler:
// it cannot refresh/replay a mutation after a 401 or a lost response.
// The caller owns the auth-only client and must never log options or tokens.
public sealed class DeyeBrokerTokenProvider : IDisposable
{
    private static readonly TimeSpan RequiredRemainingLifetime = TimeSpan.FromSeconds(180);
    private readonly HttpClient _authClient;
    private readonly DeyeBrokerTokenOptions _options;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DeyeBrokerAccessToken? _cached;
    private DateTimeOffset _observedUtc;

    public DeyeBrokerTokenProvider(HttpClient authClient, DeyeBrokerTokenOptions options, IClock clock)
    {
        _authClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).EnsureValid();
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (_authClient.BaseAddress is not { IsAbsoluteUri: true, Scheme: "https" } address
            || !address.AbsolutePath.EndsWith('/') || !string.IsNullOrEmpty(address.UserInfo)
            || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
        { throw new ArgumentException("An explicit HTTPS auth base ending in '/' is required.", nameof(authClient)); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "The returned HttpClient owns its handler; the composition root owns and disposes the client.")]
    public static HttpClient CreateAuthClient(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri || baseAddress.Scheme != Uri.UriSchemeHttps
            || !baseAddress.AbsolutePath.EndsWith('/') || !string.IsNullOrEmpty(baseAddress.UserInfo)
            || !string.IsNullOrEmpty(baseAddress.Query) || !string.IsNullOrEmpty(baseAddress.Fragment))
        { throw new ArgumentException("An explicit HTTPS auth base ending in '/' is required.", nameof(baseAddress)); }
        return new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, true)
        {
            BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 64 * 1024,
        };
    }

    public async Task<DeyeBrokerAccessToken> AcquireAsync(CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(15));
        await _gate.WaitAsync(bounded.Token).ConfigureAwait(false);
        try
        {
            var started = _clock.UtcNow;
            // A backwards clock must not make an old token appear fresh again.
            if (started < _observedUtc)
            {
                _cached = null;
                throw Unavailable();
            }
            _observedUtc = started;
            if (_cached is not null && _cached.ExpiresAtUtc - started > RequiredRemainingLifetime)
            { return _cached; }
            _cached = null;
            var body = new Dictionary<string, object?>
            {
                ["appSecret"] = _options.AppSecret, ["email"] = _options.Email,
                ["password"] = PasswordHash(_options.Password),
            };
            if (_options.CompanyId is { } company) { body["companyId"] = company; }
            using var content = JsonContent.Create(body);
            using var response = await _authClient.PostAsync(
                new Uri("account/token?appId=" + Uri.EscapeDataString(_options.AppId), UriKind.Relative), content, bounded.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { throw Unavailable(); }
            // Buffer with the configured cap before parsing; no vendor response
            // or credential-bearing URL is included in failure diagnostics.
            var json = await response.Content.ReadAsStringAsync(bounded.Token).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(json) > 64 * 1024) { throw Unavailable(); }
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw Unavailable(); }
            if (root.TryGetProperty("code", out var code)
                && (!code.TryGetInt32(out var number) || number is not (0 or 1000000)))
            { throw Unavailable(); }
            var data = root.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested : root;
            if (!data.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String
                || !data.TryGetProperty("expiresIn", out var lifetime) || !lifetime.TryGetInt32(out var seconds)
                || seconds is <= 180 or > 86400)
            { throw Unavailable(); }
            var value = token.GetString()!;
            if (value.Length is < 1 or > 4096
                || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~' or '+' or '/' or '=')))
            { throw Unavailable(); }
            var finished = _clock.UtcNow;
            var expires = started.AddSeconds(seconds); // Never extend lifetime by network latency.
            if (finished < started || expires - finished <= RequiredRemainingLifetime) { throw Unavailable(); }
            _observedUtc = finished;
            _cached = new DeyeBrokerAccessToken(value, expires);
            return _cached;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw Unavailable(); }
        catch (HttpRequestException) { throw Unavailable(); }
        catch (JsonException) { throw Unavailable(); }
        catch (InvalidOperationException) { throw Unavailable(); }
        finally { _gate.Release(); }
    }

    private static string PasswordHash(string password) => password.Length == 64 && password.All(Uri.IsHexDigit)
        ? Convert.ToHexStringLower(Convert.FromHexString(password))
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    private static InvalidOperationException Unavailable() => new("deye-broker-auth-unavailable");
    public void Dispose() => _gate.Dispose();
}
