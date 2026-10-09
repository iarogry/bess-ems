using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BatteryEms.Adapters.Telecontrol;
using BatteryEms.Application.Realtime;
using BatteryEms.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BatteryEms.Adapters.Telecontrol.Tests;

public sealed class TelecontrolTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 32, 56, TimeSpan.Zero);
    private static TelecontrolOptions Settings => new()
    {
        Username = "test-user", Password = "test-password", DeviceId = 5552,
        AssetId = "telecontrol-5552", SourceTimeZoneId = "Europe/Berlin",
    };

    [Theory]
    [InlineData("0.0", "kW", "Leistung", "Substituted", 0d)]
    [InlineData("145,6", "kW", "Leistung", "Substituted", 145.6d)]
    [InlineData("NaN", "kW", "Leistung", "ProtocolError", null)]
    [InlineData("-1", "kW", "Leistung", "ProtocolError", null)]
    [InlineData("530", "kWh", "Leistung", "ProtocolError", null)]
    [InlineData("530", "kW", "ExternalPowerSetpoint", "ProtocolError", null)]
    public void Only_measured_finite_nonnegative_kw_is_generation(string value, string unit, string name, string quality, double? expected)
    {
        var reading = TelecontrolPayload.Parse(Envelope("2026-10-08T14:32:56", value, unit, name), Settings, Now);
        Assert.Equal(expected, reading.PowerKw);
        Assert.Equal(quality, reading.Quality.Flag.ToString());
        Assert.Equal(Now, reading.Timestamp);
        Assert.False(reading.Quality.IsUsableForControl);
    }

    [Theory]
    [InlineData("2026-10-08T12:32:56Z", "Substituted")]
    [InlineData("2026-10-08T15:32:56+03:00", "Substituted")]
    [InlineData("2026-10-08T14:20:00", "Stale")]
    [InlineData("2026-10-08T15:32:56", "ProtocolError")]
    [InlineData("bad-time", "ProtocolError")]
    [InlineData("2026-10-25T02:30:00", "ProtocolError")]
    [InlineData("2026-03-29T02:30:00", "ProtocolError")]
    public void Timestamp_offsets_ages_and_dst_are_checked(string time, string quality)
    {
        Assert.Equal(quality, TelecontrolPayload.Parse(Envelope(time), Settings, Now).Quality.Flag.ToString());
    }

    [Fact]
    public void Missing_and_duplicate_data_do_not_become_zero()
    {
        Assert.Throws<JsonException>(() => TelecontrolPayload.Parse(JsonSerializer.SerializeToElement(new { data = (object?)null }), Settings, Now));
        var envelope = JsonSerializer.SerializeToElement(new { data = new { packageDateTime = "2026-10-08T14:32:56", dataPoints = new[]
        {
            new { name = "Leistung", dataBlockName = "Leistung", value = "2.0", unit = "kW" },
            new { name = "Leistung", dataBlockName = "Leistung", value = "2.0", unit = "kW" },
            new { name = "GeneratorTemperature", dataBlockName = "Temperatur12", value = "-437.0", unit = "°C" },
        } } });
        var result = TelecontrolPayload.Parse(envelope, Settings, Now);
        Assert.Null(result.PowerKw);
        Assert.False(result.Parameters[2].Available);
        Assert.Null(result.Parameters[2].NumericValue);
    }

    [Fact]
    public void Store_ages_cached_data_using_measurement_and_receipt_time()
    {
        var store = new InMemoryChpTelemetryStore();
        store.Update(TelecontrolPayload.Parse(Envelope("2026-10-08T14:32:56"), Settings, Now));
        Assert.Equal(DataQualityState.Stale, store.GetLatest("telecontrol-5552", Now.AddMinutes(3), TimeSpan.FromMinutes(2))!.Quality.Flag);
    }

    [Fact]
    public async Task Client_uses_read_only_contract_caches_token_and_refreshes_once_on_401()
    {
        using var handler = new CloudHandler();
        using var http = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Telecontrol").Returns(_ => new HttpClient(handler, disposeHandler: false));
        using var client = new TelecontrolClient(factory, Options.Create(Settings));
        Assert.Equal(0d, (await client.ReadAsync(Now, CancellationToken.None)).PowerKw);
        handler.ExpireNext = true;
        Assert.Equal(0d, (await client.ReadAsync(Now.AddSeconds(30), CancellationToken.None)).PowerKw);
        Assert.Equal(2, handler.Logins);
        Assert.Equal(3, handler.Reads);
    }

    [Fact]
    public void Disabled_adapter_does_not_register_or_replace_other_sources()
    {
        var services = new ServiceCollection();
        services.AddTelecontrolTelemetry(new ConfigurationBuilder().Build());
        Assert.DoesNotContain(services, value => value.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void Registered_client_supplies_the_gateway_required_requester_header()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Telecontrol:Enabled"] = "true", ["Telecontrol:Username"] = "test-user",
            ["Telecontrol:Password"] = "test-password", ["Telecontrol:DeviceId"] = "5552",
            ["Telecontrol:AssetId"] = "telecontrol-5552", ["Telecontrol:SourceTimeZoneId"] = "Europe/Berlin",
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTelecontrolTelemetry(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using var provider = services.BuildServiceProvider();
        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Telecontrol");
        Assert.Contains("bess-ems-telecontrol", http.DefaultRequestHeaders.UserAgent.ToString(), StringComparison.Ordinal);
    }

    private static JsonElement Envelope(string time, string value = "0.0", string unit = "kW", string name = "Leistung") =>
        JsonSerializer.SerializeToElement(new { data = new { packageDateTime = time,
            dataPoints = new[] { new { name, dataBlockName = name, value, unit, scale = 0 } } } });

    private sealed class CloudHandler : HttpMessageHandler
    {
        public int Logins { get; private set; }
        public int Reads { get; private set; }
        public bool ExpireNext { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            using var content = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (request.RequestUri!.AbsolutePath == "/api/auth/token")
            {
                Logins++;
                Assert.Equal("test-user", content.RootElement.GetProperty("userName").GetString());
                Assert.Equal("wpf", content.RootElement.GetProperty("appType").GetString());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { token = "test-token", expiration = Now.AddHours(1) }) };
            }
            Assert.Equal("/api?call=GetLastDatapoints", request.RequestUri.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(5552, content.RootElement.GetProperty("deviceId").GetInt32());
            Reads++;
            if (ExpireNext) { ExpireNext = false; return new HttpResponseMessage(HttpStatusCode.Unauthorized); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Envelope("2026-10-08T14:32:56")) };
        }
    }
}
