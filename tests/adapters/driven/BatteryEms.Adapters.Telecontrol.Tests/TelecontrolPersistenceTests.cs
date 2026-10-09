using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BatteryEms.Adapters.Telecontrol;
using BatteryEms.Application.Realtime;
using BatteryEms.Application.Site;
using BatteryEms.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BatteryEms.Adapters.Telecontrol.Tests;

public sealed class TelecontrolPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);
    private static TelecontrolOptions Settings => new()
    {
        AssetId = "telecontrol-5552", DeviceId = 5552, SiteId = "site-a",
        Username = "test-user", Password = "secret-not-for-storage", SourceTimeZoneId = "UTC",
    };

    [Theory]
    [InlineData(DataQualityState.Valid, "valid", 14.2)]
    [InlineData(DataQualityState.Substituted, "substituted", 14.2)]
    [InlineData(DataQualityState.Stale, "stale", 14.2)]
    [InlineData(DataQualityState.ProtocolError, "source_error", null)]
    public async Task History_preserves_quality_parameters_messages_and_upserts_identical_samples(
        DataQualityState flag, string quality, double? power)
    {
        var telemetry = new ChpTelemetry("telecontrol-5552", 5552, Now, Now.AddSeconds(10), Now.ToString("O"),
            power, new DataQuality(flag, "test-reason"),
            [new ChpParameter("Temperature", "°C", "84,2", 84.2, true), new ChpParameter("Status", "", "Running", null, true),
             new ChpParameter("Unavailable", "°C", "---", null, false)],
            [new ChpMessage("Engine warning", 7, 2)]);
        var row = TelecontrolMeasurement.Create(telemetry, Settings);
        Assert.Equal("site-a", row.SiteId);
        Assert.Equal("telecontrol", row.Source);
        Assert.Equal("chp", row.InstrumentType);
        Assert.Equal("5552", row.InstrumentId);
        Assert.Equal("chp_power", row.Metric);
        Assert.Equal("kW", row.Unit);
        Assert.Equal(Now, row.Timestamp);
        Assert.Equal(power, row.Value);
        Assert.Equal(quality, row.Quality);
        AssertMetadata(row.MetadataJson!);
        var store = new InMemorySiteMeasurementStore();
        await store.AppendAsync([row, row], CancellationToken.None);
        Assert.Single(await store.QueryAsync(new SiteMeasurementQuery("site-a", Now.AddSeconds(-1), Now.AddSeconds(1)), CancellationToken.None));
        var unassigned = Settings;
        unassigned.SiteId = null;
        Assert.Equal("unassigned:telecontrol:5552", TelecontrolMeasurement.Create(telemetry, unassigned).SiteId);
    }

    private static void AssertMetadata(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(Now.AddSeconds(10), root.GetProperty("received_at").GetDateTimeOffset());
        Assert.Equal("test-reason", root.GetProperty("reason").GetString());
        Assert.Equal(3, root.GetProperty("parameters").GetArrayLength());
        Assert.Equal("84,2", root.GetProperty("parameters")[0].GetProperty("text_value").GetString());
        Assert.False(root.GetProperty("parameters")[2].GetProperty("available").GetBoolean());
        Assert.Equal(7, root.GetProperty("messages")[0].GetProperty("code").GetInt32());
        Assert.DoesNotContain("secret-not-for-storage", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Polling_writes_history_and_keeps_live_snapshots_when_storage_or_provider_fails(bool failStorage, bool failProvider)
    {
        using var handler = new CloudHandler(failProvider);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Telecontrol").Returns(_ => new HttpClient(handler, disposeHandler: false));
        using var client = new TelecontrolClient(factory, Options.Create(Settings));
        var chpStore = new InMemoryChpTelemetryStore();
        var siteStore = new InMemorySiteTelemetryStore(TimeSpan.FromMinutes(2));
        var captured = new TaskCompletionSource<SiteMeasurementReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        var measurements = CreateMeasurements(captured, failStorage);
        using var service = new TelecontrolPollingService(client, Options.Create(Settings), siteStore, chpStore,
            new FixedTimeProvider(), Substitute.For<ILogger<TelecontrolPollingService>>(), measurements);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var row = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(failProvider ? "source_error" : "substituted", row.Quality);
            Assert.Equal(failProvider ? (double?)null : 14.2, row.Value);
            Assert.Equal(row.Value, chpStore.GetLatest("telecontrol-5552", Now, TimeSpan.FromMinutes(2))!.PowerKw);
            Assert.Equal(row.Value, siteStore.GetLatest("telecontrol-5552", Now)!.Telemetry.PvPowerKw);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private static ISiteMeasurementStore CreateMeasurements(TaskCompletionSource<SiteMeasurementReading> captured, bool fail)
    {
        var store = Substitute.For<ISiteMeasurementStore>();
        store.AppendAsync(Arg.Any<IReadOnlyList<SiteMeasurementReading>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            captured.TrySetResult(Assert.Single(call.Arg<IReadOnlyList<SiteMeasurementReading>>()));
            return fail ? Task.FromException(new InvalidOperationException("Storage unavailable")) : Task.CompletedTask;
        });
        return store;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Site_binding_is_loaded_and_conflicts_are_rejected(bool conflict)
    {
        var values = new Dictionary<string, string?>
        {
            ["Telecontrol:Enabled"] = "true", ["Telecontrol:Username"] = "test-user", ["Telecontrol:Password"] = "test-password",
            ["Telecontrol:DeviceId"] = "5552", ["Telecontrol:AssetId"] = "telecontrol-5552", ["Telecontrol:SourceTimeZoneId"] = "UTC",
            ["Dashboard:Sites:0:SiteId"] = "site-a", ["Dashboard:Sites:0:Sources:0:Kind"] = "chp",
            ["Dashboard:Sites:0:Sources:0:TelemetryId"] = "telecontrol-5552",
        };
        if (conflict) { values["Telecontrol:SiteId"] = "site-b"; }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTelecontrolTelemetry(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        using var provider = services.BuildServiceProvider();
        if (conflict) { Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<TelecontrolOptions>>().Value); }
        else { Assert.Equal("site-a", provider.GetRequiredService<IOptions<TelecontrolOptions>>().Value.SiteId); }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CloudHandler(bool failProvider) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/token")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { token = "test-token" }) });
            }
            return Task.FromResult(new HttpResponseMessage(failProvider ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new { packageDateTime = Now.ToString("O"),
                    dataPoints = new[] { new { name = "Leistung", dataBlockName = "Leistung", unit = "kW", value = "14.2" } } } }),
            });
        }
    }
}
