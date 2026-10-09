using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatteryEms.Application.Time;
using Xunit;

namespace BatteryEms.Adapters.DeyeCloud.Tests;

public sealed class DeyeBrokerTokenProviderTests
{
    [Fact]
    public async Task Concurrent_acquisition_uses_one_auth_call_and_redacts_diagnostics()
    {
        using var vendor = new AuthVendor();
        using var client = Client(vendor);
        using var provider = new DeyeBrokerTokenProvider(client, Options(), new Clock());
        var tokens = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => provider.AcquireAsync(CancellationToken.None)));
        Assert.Equal(1, vendor.Calls);
        Assert.All(tokens, token => Assert.Same(tokens[0], token));
        Assert.DoesNotContain(tokens[0].Value, tokens[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-test", Options().ToString(), StringComparison.Ordinal);
        Assert.Equal("/v1/account/token?appId=app%2Ftest", vendor.Path);
        using var body = JsonDocument.Parse(vendor.Body!);
        Assert.Equal(64, body.RootElement.GetProperty("password").GetString()!.Length);
        Assert.DoesNotContain("password-test", vendor.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_is_only_explicit_acquisition_and_previous_token_remains_immutable()
    {
        using var vendor = new AuthVendor();
        using var client = Client(vendor);
        var clock = new Clock();
        using var provider = new DeyeBrokerTokenProvider(client, Options(), clock);
        var first = await provider.AcquireAsync(CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddSeconds(3420); // Exactly the conservative lifetime margin.
        var second = await provider.AcquireAsync(CancellationToken.None);
        Assert.Equal(2, vendor.Calls);
        Assert.Equal("token-1", first.Value);
        Assert.Equal("token-2", second.Value);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"accessToken\":\"token\"}")]
    [InlineData("{\"accessToken\":\"token\",\"expiresIn\":180}")]
    [InlineData("{\"accessToken\":\"token\",\"expiresIn\":-1}")]
    [InlineData("{\"accessToken\":\"token\",\"expiresIn\":86401}")]
    [InlineData("{\"accessToken\":\"token\",\"expiresIn\":\"3600\"}")]
    [InlineData("{\"accessToken\":\"token with spaces\",\"expiresIn\":3600}")]
    [InlineData("{\"code\":500,\"accessToken\":\"token\",\"expiresIn\":3600}")]
    [InlineData("not-json-secret-test")]
    public async Task Invalid_or_insufficient_lifetime_is_not_cached(string response)
    {
        using var vendor = new AuthVendor { Response = response };
        using var client = Client(vendor);
        using var provider = new DeyeBrokerTokenProvider(client, Options(), new Clock());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.AcquireAsync(CancellationToken.None));
        Assert.Equal("deye-broker-auth-unavailable", error.Message);
        Assert.Null(error.InnerException);
        vendor.Response = null;
        Assert.Equal("token-2", (await provider.AcquireAsync(CancellationToken.None)).Value);
        Assert.Equal(2, vendor.Calls);
    }

    [Fact]
    public async Task Unauthorized_auth_response_is_not_retried()
    {
        using var vendor = new AuthVendor { Status = HttpStatusCode.Unauthorized };
        using var client = Client(vendor);
        using var provider = new DeyeBrokerTokenProvider(client, Options(), new Clock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.AcquireAsync(CancellationToken.None));
        Assert.Equal(1, vendor.Calls);
    }

    [Fact]
    public async Task Backwards_clock_refuses_cached_token_without_network_call()
    {
        using var vendor = new AuthVendor();
        using var client = Client(vendor);
        var clock = new Clock();
        using var provider = new DeyeBrokerTokenProvider(client, Options(), clock);
        await provider.AcquireAsync(CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddSeconds(-1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.AcquireAsync(CancellationToken.None));
        Assert.Equal(1, vendor.Calls);
    }

    [Fact]
    public async Task Network_latency_does_not_extend_vendor_expiry()
    {
        using var vendor = new AuthVendor { Response = "{\"data\":{\"accessToken\":\"token\",\"expiresIn\":181}}" };
        using var client = Client(vendor);
        var clock = new Clock();
        vendor.BeforeResponse = () => clock.UtcNow = clock.UtcNow.AddSeconds(2);
        using var provider = new DeyeBrokerTokenProvider(client, Options(), clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.AcquireAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Password_protocol_preserves_lowercase_sha256_and_does_not_double_hash(bool alreadyHashed)
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("password-test")));
        using var vendor = new AuthVendor();
        using var client = Client(vendor);
        var options = new DeyeBrokerTokenOptions
        {
            AppId = "app", AppSecret = "secret-test", Email = "test@example.invalid",
            Password = alreadyHashed ? Convert.ToHexString(Convert.FromHexString(expected)) : "password-test", CompanyId = 42,
        };
        using var provider = new DeyeBrokerTokenProvider(client, options, new Clock());
        await provider.AcquireAsync(CancellationToken.None);
        using var body = JsonDocument.Parse(vendor.Body!);
        Assert.Equal(expected, body.RootElement.GetProperty("password").GetString());
        Assert.Equal(42, body.RootElement.GetProperty("companyId").GetInt64());
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_without_auth_call()
    {
        using var vendor = new AuthVendor();
        using var client = Client(vendor);
        using var provider = new DeyeBrokerTokenProvider(client, Options(), new Clock());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.AcquireAsync(cancelled.Token));
        Assert.Equal(0, vendor.Calls);
    }

    private static DeyeBrokerTokenOptions Options() => new()
    {
        AppId = "app/test", AppSecret = "secret-test", Email = "test@example.invalid", Password = "password-test",
    };

    private static HttpClient Client(HttpMessageHandler handler) => new(handler, false)
    {
        BaseAddress = new Uri("https://vendor.invalid/v1/"), MaxResponseContentBufferSize = 64 * 1024,
    };

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class AuthVendor : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? Response { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Action? BeforeResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Path = request.RequestUri!.PathAndQuery;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            BeforeResponse?.Invoke();
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Response ?? $"{{\"code\":0,\"data\":{{\"accessToken\":\"token-{Calls}\",\"expiresIn\":3600}}}}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
