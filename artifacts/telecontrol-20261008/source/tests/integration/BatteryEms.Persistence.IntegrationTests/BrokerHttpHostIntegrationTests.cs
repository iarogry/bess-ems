using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using BatteryEms.Adapters.DeyeCloud;
using BatteryEms.Adapters.Persistence;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;
using BatteryEms.BrokerHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BatteryEms.Persistence.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public sealed class BrokerHttpHostIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 20, 55, 25, TimeSpan.Zero);
    private const string SiteId = "broker-http-site";
    private const string LegacyToken = "legacy-test-token-no-real-secret-12345678";
    private const string ProductToken = "product-test-token-no-real-secret-12345678";

    [Fact]
    public async Task Default_disabled_host_has_no_mutation_route_and_reports_unready()
    {
        await using var app = DeviceWriteBrokerHostBuilder.BuildApp([], builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/v1/device-writes", Request())).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.GetAsync(new Uri("/healthz", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public void Enabled_host_rejects_missing_tls_binding_or_credentials_before_opening_database()
    {
        Assert.Throws<ArgumentException>(() => DeviceWriteBrokerHostBuilder.BuildApp([], builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Broker:Enabled"] = "true",
                ["Broker:PersistenceConnectionString"] = "Host=localhost;Database=invalid",
            })));
        var configured = new BrokerServerOptions
        {
            Enabled = true,
            ListenUrl = new Uri("http://localhost:5443"),
            PersistenceConnectionString = "Host=localhost;Database=invalid",
        };
        configured.Clients.Add(new BrokerClientOptions
        {
            Role = ActivationWriterAuthority.LegacyRunner, OwnerId = "legacy", Token = LegacyToken,
            Sites = { SiteId },
        });
        configured.Clients.Add(new BrokerClientOptions
        {
            Role = ActivationWriterAuthority.ProductAgent, OwnerId = "product", Token = ProductToken,
            Sites = { SiteId },
        });
        Assert.Throws<ArgumentException>(() => configured.EnsureValid());
    }

    [Fact]
    public async Task Http_boundary_requires_tls_token_site_and_strict_bounded_json()
    {
        var connection = ConnectionString();
        await using var dataSource = NpgsqlDataSource.Create(connection);
        await using var app = BuildEnabledApp(connection);
        await app.StartAsync();
        using var insecureClient = app.GetTestClient();
        insecureClient.BaseAddress = new Uri("http://broker.test/");
        using var client = app.GetTestClient();
        var body = Request();
        using (var insecure = await insecureClient.PostAsJsonAsync("/v1/device-writes", body))
        { Assert.Equal(HttpStatusCode.Forbidden, insecure.StatusCode); }
        client.BaseAddress = new Uri("https://broker.test/");
        using (var anonymous = await client.PostAsJsonAsync("/v1/device-writes", body))
        { Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-test-token-xxxxxxxxxxxxxxxxxxxxx");
        using (var invalid = await client.PostAsJsonAsync("/v1/device-writes", body))
        { Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode); }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LegacyToken);
        using (var foreign = await client.PostAsJsonAsync("/v1/device-writes", body with { SiteId = "foreign-site" }))
        { Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode); }
        using (var raw = await client.PostAsJsonAsync("/v1/device-writes", new
        {
            request = body, vendorUrl = "https://fake-vendor.invalid/order/sys/tou/update",
        })) { Assert.Equal(HttpStatusCode.BadRequest, raw.StatusCode); }
        await using var count = dataSource.CreateCommand("SELECT COUNT(*) FROM device_write_broker_attempts WHERE attempt_id = $1;");
        count.Parameters.AddWithValue(body.AttemptId);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Legacy_http_identity_is_server_bound_and_default_driver_sends_nothing()
    {
        var connection = ConnectionString();
        await using var dataSource = NpgsqlDataSource.Create(connection);
        await using var app = BuildEnabledApp(connection, testDriver: true);
        await app.StartAsync();
        using (var statusClient = app.GetTestClient())
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                (await statusClient.GetAsync(new Uri("/healthz", UriKind.Relative))).StatusCode);
            Assert.Equal(HttpStatusCode.OK,
                (await statusClient.GetAsync(new Uri("/livez", UriKind.Relative))).StatusCode);
        }
        var safety = new DapperActivationWriterSafetyStore(dataSource);
        Assert.True(await safety.CompareExchangeStateAsync(
            ActivationSafetyState.InitialFailClosed(SiteId, Now, "operator-a", "test broker") with
            { KillSwitchEngaged = false }, null, CancellationToken.None));
        var lease = await safety.TryAcquireLeaseAsync(SiteId, "legacy-owner", Now,
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(lease.Acquired);
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://broker.test/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LegacyToken);
        var body = Request() with { FencingToken = lease.Lease!.FencingToken };
        using var response = await client.PostAsJsonAsync("/v1/device-writes", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DeviceWriteBrokerExecutionResult>();
        Assert.NotNull(result);
        Assert.True(result.Admitted);
        Assert.Equal(DeviceWriteBrokerAttemptState.NotSent, result.State); // Test driver preflight denies all writes.
        Assert.False(result.DriverInvoked);
        using var replay = await client.PostAsJsonAsync("/v1/device-writes", body);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("device-write-broker-replay-no-execution",
            (await replay.Content.ReadFromJsonAsync<DeviceWriteBrokerExecutionResult>())!.OutcomeCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ProductToken);
        using var wrongRole = await client.PostAsJsonAsync("/v1/device-writes", body with { AttemptId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, wrongRole.StatusCode); // Product role needs an exact claim.
        await using var audit = dataSource.CreateCommand("SELECT writer_owner_id, authority, state FROM device_write_broker_attempts WHERE attempt_id = $1;");
        audit.Parameters.AddWithValue(body.AttemptId);
        await using var reader = await audit.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("legacy-owner", reader.GetString(0));
        Assert.Equal("LegacyRunner", reader.GetString(1));
        Assert.Equal("NotSent", reader.GetString(2));
    }

    [Fact]
    public async Task Enabled_shell_without_device_driver_returns_unready_without_consuming_authorization()
    {
        var connection = ConnectionString();
        await using var dataSource = NpgsqlDataSource.Create(connection);
        await using var app = BuildEnabledApp(connection);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://broker.test/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LegacyToken);
        var body = Request();
        using var response = await client.PostAsJsonAsync("/v1/device-writes", body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var count = dataSource.CreateCommand("SELECT COUNT(*) FROM device_write_broker_attempts WHERE attempt_id = $1;");
        count.Parameters.AddWithValue(body.AttemptId);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Concrete_session_auth_failure_returns_fixed_503_before_creating_attempt()
    {
        var connection = ConnectionString();
        using var vendor = new AuthVendor { Status = HttpStatusCode.Unauthorized };
        using var authClient = new HttpClient(vendor, false) { BaseAddress = new Uri("https://vendor.invalid/v1/") };
        using var tokens = TokenProvider(authClient);
        await using var dataSource = NpgsqlDataSource.Create(connection);
        await using var app = BuildEnabledApp(connection, configureServices: services =>
            services.AddSingleton<IBrokerDriverSessionFactory>(sp => new DeyeBrokerDriverSessionFactory(
                authClient.BaseAddress, Device(), tokens, sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<IDeviceWriteBrokerMutationGate>())));
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://broker.test/");
        var body = Request();
        // Unauthenticated or malformed callers cannot acquire vendor tokens.
        using (var unauthenticated = await client.PostAsJsonAsync("/v1/device-writes", body))
        { Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode); }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LegacyToken);
        using (var malformed = await client.PostAsJsonAsync("/v1/device-writes", body with { AttemptId = Guid.Empty }))
        { Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode); }
        Assert.Equal(0, vendor.Calls);
        using var response = await client.PostAsJsonAsync("/v1/device-writes", body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("device-write-broker-auth-unavailable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-vendor-body", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1, vendor.Calls);
        await using var count = dataSource.CreateCommand("SELECT COUNT(*) FROM device_write_broker_attempts WHERE attempt_id = $1;");
        count.Parameters.AddWithValue(body.AttemptId);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Concrete_factory_creates_distinct_sessions_with_cached_token_and_exact_site_binding()
    {
        using var vendor = new AuthVendor();
        using var client = new HttpClient(vendor, false) { BaseAddress = new Uri("https://vendor.invalid/v1/") };
        using var tokens = TokenProvider(client);
        var factory = new DeyeBrokerDriverSessionFactory(client.BaseAddress, Device(), tokens, new FixedClock(), new DenyGate());
        Assert.True(factory.SupportsSite(SiteId));
        Assert.False(factory.SupportsSite("foreign-site"));
        using var first = await factory.OpenAsync(CancellationToken.None);
        using var second = await factory.OpenAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.IsType<TokenBoundBrokerDriver>(first.Driver);
        Assert.NotSame(first.Driver, second.Driver);
        Assert.Equal(1, vendor.Calls);
        var disabled = new DeyeBrokerDriverSessionFactory(client.BaseAddress, Device() with { WriteEnabled = false }, tokens, new FixedClock(), new DenyGate());
        Assert.False(disabled.SupportsSite(SiteId));
        Assert.Null(await disabled.OpenAsync(CancellationToken.None));
        Assert.Equal(1, vendor.Calls);
    }

    [Fact]
    public void Deye_configuration_requires_two_switches_and_https_before_database_startup()
    {
        Assert.Throws<ArgumentException>(() => new BrokerDeyeOptions { WriteEnabled = true }.EnsureValid());
        Assert.Throws<ArgumentException>(() => new BrokerDeyeOptions { Enabled = true, BaseAddress = new Uri("http://vendor.invalid/") }.EnsureValid());
        Assert.Throws<ArgumentException>(() => new BrokerDeyeOptions { Enabled = true, BaseAddress = new Uri("https://vendor.invalid/") }.EnsureValid());
    }

    [Fact]
    public async Task Database_delay_cannot_start_driver_with_insufficient_token_lifetime()
    {
        var window = new ShadowTouWindow("Z1", []);
        var plan = new ShadowPlanSnapshot(SiteId, new DateOnly(2026, 9, 23), false, [], [window]);
        var request = new DeviceWriteBrokerBeginRequest(Guid.NewGuid(), SiteId, plan.DeliveryDate,
            "Z1", new string('A', 64), ActivationWriterAuthority.LegacyRunner, "legacy-owner", 1, 1, null, Now);
        var envelope = new DeviceWriteBrokerEnvelope(request, plan, window);
        var driver = new TokenBoundBrokerDriver(new ThrowingDriver(), Now.AddSeconds(150), new FixedClock());
        Assert.False(await driver.PreflightAsync(envelope, CancellationToken.None));
        driver = new TokenBoundBrokerDriver(new ThrowingDriver(), Now.AddSeconds(120), new FixedClock());
        Assert.Equal(DeviceWriteBrokerReadback.Unknown, await driver.WriteOnceAndVerifyAsync(envelope, CancellationToken.None));
    }

    private sealed class ThrowingDriver : IDeviceWriteBrokerDriver
    {
        public Task<bool> PreflightAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Expiry guard must deny before touching the driver.");
        public Task<DeviceWriteBrokerReadback> WriteOnceAndVerifyAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Expiry guard must deny before touching the driver.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_composition_uses_shared_durable_gate_and_explicit_write_switch(bool enableWrite)
    {
        await using var app = BuildEnabledApp(ConnectionString(), configureDeye: true, enableWrite: enableWrite);
        await app.StartAsync();
        var sessions = app.Services.GetRequiredService<IBrokerDriverSessionFactory>();
        Assert.Equal(enableWrite, sessions.SupportsSite(SiteId));
        if (enableWrite) { Assert.IsType<DeyeBrokerDriverSessionFactory>(sessions); }
        Assert.Same(app.Services.GetRequiredService<IDeviceWriteBrokerAttemptStore>(),
            app.Services.GetRequiredService<IDeviceWriteBrokerMutationGate>());
        // Resolving a production factory performs no vendor authentication.
        // With the write switch off, the HTTP route remains closed before admission.
        if (!enableWrite)
        {
            using var client = app.GetTestClient();
            client.BaseAddress = new Uri("https://broker.test/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LegacyToken);
            using var response = await client.PostAsJsonAsync("/v1/device-writes", Request());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }

    private static DeyeBrokerDeviceOptions Device() => new(SiteId, "fake-station", "fake-master", "fake-slave", WriteEnabled: true);
    private static DeyeBrokerTokenProvider TokenProvider(HttpClient client) => new(client, new DeyeBrokerTokenOptions
    {
        AppId = "fake-app", AppSecret = "fake-secret", Email = "test@example.invalid", Password = "fake-password",
    }, new FixedClock());

    private sealed class DenyGate : IDeviceWriteBrokerMutationGate
    {
        public Task<bool> CanSendAsync(DeviceWriteBrokerBeginRequest request, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class AuthVendor : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("/v1/account/token", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Status == HttpStatusCode.OK
                    ? "{\"accessToken\":\"fake-access-token\",\"expiresIn\":3600}" : "secret-vendor-body", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static WebApplication BuildEnabledApp(string connection, bool testDriver = false,
        Action<IServiceCollection>? configureServices = null, bool configureDeye = false, bool enableWrite = false) =>
        DeviceWriteBrokerHostBuilder.BuildApp([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Broker:Enabled"] = "true",
                ["Broker:PersistenceConnectionString"] = connection,
                ["Broker:ListenUrl"] = "https://localhost:5443",
                ["Broker:Clients:0:Role"] = "LegacyRunner",
                ["Broker:Clients:0:OwnerId"] = "legacy-owner",
                ["Broker:Clients:0:Token"] = LegacyToken,
                ["Broker:Clients:0:Sites:0"] = SiteId,
                ["Broker:Clients:1:Role"] = "ProductAgent",
                ["Broker:Clients:1:OwnerId"] = "product-owner",
                ["Broker:Clients:1:Token"] = ProductToken,
                ["Broker:Clients:1:Sites:0"] = SiteId,
            });
            if (configureDeye)
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Broker:Deye:Enabled"] = "true", ["Broker:Deye:WriteEnabled"] = enableWrite ? "true" : "false",
                    ["Broker:Deye:BaseAddress"] = "https://vendor.invalid/v1/", ["Broker:Deye:SiteId"] = SiteId,
                    ["Broker:Deye:StationId"] = "fake-station", ["Broker:Deye:MasterSerial"] = "fake-master", ["Broker:Deye:SlaveSerial"] = "fake-slave",
                    ["Broker:Deye:AppId"] = "fake-app", ["Broker:Deye:AppSecret"] = "fake-secret",
                    ["Broker:Deye:Email"] = "test@example.invalid", ["Broker:Deye:Password"] = "fake-password",
                });
            }
        }, services =>
        {
            services.AddSingleton<IClock>(new FixedClock());
            if (testDriver) { services.AddSingleton<IDeviceWriteBrokerDriver>(_ => new DenyPreflightTestDriver()); }
            configureServices?.Invoke(services);
        });

    private static BrokerWriteRequest Request() => new(Guid.NewGuid(), SiteId, new DateOnly(2026, 9, 23),
        "Z1", new string('A', 64), 1, 1, null);

    private static string ConnectionString() => PersistenceOptions.FromHostPort(
        Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "127.0.0.1",
        int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var port) ? port : 5432,
        Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "bessems",
        Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "bessems",
        Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "bessems").ConnectionString;

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class DenyPreflightTestDriver : IDeviceWriteBrokerDriver
    {
        public Task<bool> PreflightAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<DeviceWriteBrokerReadback> WriteOnceAndVerifyAsync(
            DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The HTTP test double has no mutation capability.");
    }
}
