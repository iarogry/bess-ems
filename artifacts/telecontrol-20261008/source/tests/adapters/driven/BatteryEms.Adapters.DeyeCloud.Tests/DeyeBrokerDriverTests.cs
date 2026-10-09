using System.Net;
using System.Text.Json.Nodes;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;
using Xunit;

namespace BatteryEms.Adapters.DeyeCloud.Tests;

public sealed class DeyeBrokerDriverTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 20, 55, 25, TimeSpan.Zero);
    private static readonly string[] Starts = ["00:00", "04:00", "08:00", "12:00", "16:00", "20:00"];
    private static readonly string[] FirstTwoReadIds = ["201", "202"];

    [Fact]
    public async Task Disabled_driver_never_contacts_vendor()
    {
        using var vendor = new Vendor();
        using var client = Client(vendor);
        var driver = Driver(client, enabled: false);
        Assert.False(await driver.PreflightAsync(Envelope(), CancellationToken.None));
        Assert.Equal(DeviceWriteBrokerReadback.Unknown, await driver.WriteOnceAndVerifyAsync(Envelope(), CancellationToken.None));
        Assert.Equal(0, vendor.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_update_preserves_time_setting_pairs_and_requires_fresh_matching_readback(bool sellSupported)
    {
        using var vendor = new Vendor { SellSupported = sellSupported };
        using var client = Client(vendor);
        var driver = Driver(client);
        var envelope = Envelope();
        Assert.True(await driver.PreflightAsync(envelope, CancellationToken.None));
        Assert.Equal(DeviceWriteBrokerReadback.Matched, await driver.WriteOnceAndVerifyAsync(envelope, CancellationToken.None));
        Assert.Equal(2, vendor.TelemetryCalls); // Admission preflight + actual write-boundary preflight.
        Assert.Equal(1, vendor.Updates);
        Assert.Equal(1, vendor.ReadOrders);
        Assert.Equal("00:00", vendor.Wire![5]!["time"]!.ToString());
        Assert.Equal("04:00", vendor.Wire[0]!["time"]!.ToString());
        Assert.Equal(envelope.Window.Intervals[1].PowerWatts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            vendor.Wire[0]!["power"]!.ToString());
        Assert.All(vendor.Wire.OfType<JsonObject>(), row => Assert.Equal(sellSupported, row.ContainsKey("enableSell")));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("alarm")]
    [InlineData("missing-alarm")]
    [InlineData("missing-soc")]
    [InlineData("low-soc")]
    [InlineData("power")]
    [InlineData("wrong-slave")]
    public async Task Live_gate_failure_blocks_update(string failure)
    {
        using var vendor = new Vendor { Failure = failure };
        using var client = Client(vendor);
        var driver = Driver(client);
        Assert.False(await driver.PreflightAsync(Envelope(), CancellationToken.None));
        Assert.Equal(DeviceWriteBrokerReadback.Unknown, await driver.WriteOnceAndVerifyAsync(Envelope(), CancellationToken.None));
        Assert.Equal(0, vendor.Updates);
    }

    [Fact]
    public async Task Previously_good_preflight_is_not_reused_after_telemetry_becomes_stale()
    {
        using var vendor = new Vendor();
        using var client = Client(vendor);
        var driver = Driver(client);
        Assert.True(await driver.PreflightAsync(Envelope(), CancellationToken.None));
        vendor.Failure = "stale";
        Assert.Equal(DeviceWriteBrokerReadback.Unknown, await driver.WriteOnceAndVerifyAsync(Envelope(), CancellationToken.None));
        Assert.Equal(2, vendor.TelemetryCalls);
        Assert.Equal(0, vendor.Updates);
    }

    [Fact]
    public async Task Completed_mismatching_read_is_replaced_by_new_order_without_repeating_update()
    {
        using var vendor = new Vendor { MismatchFirst = true };
        using var client = Client(vendor);
        Assert.Equal(DeviceWriteBrokerReadback.Matched,
            await Driver(client).WriteOnceAndVerifyAsync(Envelope(), CancellationToken.None));
        Assert.Equal(1, vendor.Updates);
        Assert.Equal(2, vendor.ReadOrders);
        Assert.Equal(FirstTwoReadIds, vendor.CompletedReads);
    }

    [Fact]
    public async Task Status_666_with_only_mismatching_readbacks_remains_unknown()
    {
        using var vendor = new Vendor { AlwaysMismatch = true };
        using var client = Client(vendor);
        Assert.Equal(DeviceWriteBrokerReadback.Unknown,
            await Driver(client).WriteOnceAndVerifyAsync(Envelope(), CancellationToken.None));
        Assert.Equal(1, vendor.Updates);
        Assert.Equal(3, vendor.ReadOrders);
        Assert.Equal(3, vendor.CompletedReads.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Lost_update_response_is_not_retried_inside_driver()
    {
        using var vendor = new Vendor { LoseUpdateResponse = true };
        using var client = Client(vendor);
        await Assert.ThrowsAsync<HttpRequestException>(() => Driver(client).WriteOnceAndVerifyAsync(Envelope(), CancellationToken.None));
        Assert.Equal(1, vendor.Updates);
        Assert.Equal(0, vendor.ReadOrders);
    }

    [Fact]
    public async Task Unauthorized_update_is_not_reauthenticated_or_retried()
    {
        using var vendor = new Vendor { UnauthorizedUpdate = true };
        using var client = Client(vendor);
        await Assert.ThrowsAsync<HttpRequestException>(() => Driver(client).WriteOnceAndVerifyAsync(Envelope(), CancellationToken.None));
        Assert.Equal(1, vendor.Updates);
        Assert.Equal(0, vendor.ReadOrders);
    }

    [Fact]
    public async Task Failed_order_status_does_not_override_independent_matching_device_readback()
    {
        using var vendor = new Vendor { OrderFailed = true };
        using var client = Client(vendor);
        Assert.Equal(DeviceWriteBrokerReadback.Matched,
            await Driver(client).WriteOnceAndVerifyAsync(Envelope(), CancellationToken.None));
        Assert.Equal(1, vendor.Updates);
        Assert.Equal(1, vendor.ReadOrders);
    }

    private static HttpClient Client(Vendor vendor) => new(vendor) { BaseAddress = new Uri("https://fake-deye.invalid/") };

    private static DeyeCloudDeviceWriteBrokerDriver Driver(HttpClient client, bool enabled = true) =>
        new(client, new DeyeBrokerDeviceOptions("site", "test-station", "test-master", "test-slave", enabled, MaximumReadOrders: 3), new Clock(), new AllowTestGate());

    private static DeviceWriteBrokerEnvelope Envelope()
    {
        var windows = Enumerable.Range(1, 4).Select(index => new ShadowTouWindow($"Z{index}",
            Starts.Select((start, row) =>
                new ShadowTouInterval(start, true, row == 1, row == 3, 10007 + row * 100, 30 + row, 290)).ToArray())).ToArray();
        var plan = new ShadowPlanSnapshot("site", new DateOnly(2026, 9, 23), true, [], windows);
        var request = new DeviceWriteBrokerBeginRequest(Guid.NewGuid(), "site", plan.DeliveryDate, "Z1",
            ActivationPayloadIntegrity.ComputeHash(plan), ActivationWriterAuthority.LegacyRunner, "writer", 1, 1, null, Now);
        return new(request, plan, windows[0]);
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class AllowTestGate : IDeviceWriteBrokerMutationGate
    {
        public Task<bool> CanSendAsync(DeviceWriteBrokerBeginRequest request, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class Vendor : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int Updates { get; private set; }
        public int ReadOrders { get; private set; }
        public int TelemetryCalls { get; private set; }
        public string? Failure { get; set; }
        public bool SellSupported { get; init; }
        public bool MismatchFirst { get; init; }
        public bool AlwaysMismatch { get; init; }
        public bool LoseUpdateResponse { get; init; }
        public bool UnauthorizedUpdate { get; init; }
        public bool OrderFailed { get; init; }
        public JsonArray? Wire { get; private set; }
        public List<string> CompletedReads { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = request.Content is null ? new JsonObject() : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            JsonObject result;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/station/device":
                    result = new JsonObject { ["deviceListItems"] = new JsonArray(Device("test-master"), Device(Failure == "wrong-slave" ? "foreign-slave" : "test-slave")) };
                    break;
                case "/device/latest":
                    TelemetryCalls++;
                    result = new JsonObject { ["deviceDataList"] = new JsonArray(Telemetry("test-master"), Telemetry("test-slave")) };
                    break;
                case "/config/tou":
                    var settings = new JsonArray(Enumerable.Range(0, 6).Select(_ => (JsonNode)new JsonObject()).ToArray());
                    if (SellSupported) { foreach (var item in settings) { item!["enableSell"] = false; } }
                    result = new JsonObject { ["timeUseSettingItems"] = settings };
                    break;
                case "/config/system": result = new JsonObject(); break;
                case "/order/sys/tou/update":
                    Updates++;
                    Assert.Equal("test-master", body["deviceSn"]!.ToString());
                    Wire = body["timeUseSettingItems"]!.DeepClone().AsArray();
                    if (UnauthorizedUpdate) { return new HttpResponseMessage(HttpStatusCode.Unauthorized); }
                    if (LoseUpdateResponse) { throw new HttpRequestException("Simulated lost response after accepting update."); }
                    result = new JsonObject { ["orderId"] = 100 };
                    break;
                case "/order/100": result = new JsonObject { ["orderId"] = 100, ["status"] = OrderFailed ? 400 : 666, ["success"] = !OrderFailed }; break;
                case "/strategy/dynamicControl/read":
                    ReadOrders++;
                    result = new JsonObject { ["orderId"] = 200 + ReadOrders };
                    break;
                case "/strategy/dynamicControl/readResult":
                    var id = body["orderId"]!.ToString();
                    Assert.DoesNotContain(id, CompletedReads);
                    CompletedReads.Add(id);
                    var actual = Wire!.DeepClone().AsArray();
                    foreach (var row in actual) { row!["power"] = row["power"]!.GetValue<int>() / 10 * 10; }
                    if (AlwaysMismatch || (MismatchFirst && ReadOrders == 1)) { actual[0]!["soc"] = 99; }
                    result = new JsonObject { ["orderId"] = int.Parse(id, System.Globalization.CultureInfo.InvariantCulture),
                        ["success"] = true, ["touAction"] = "on", ["timeUseSettingItems"] = actual };
                    break;
                default: throw new InvalidOperationException("Unexpected vendor endpoint.");
            }
            result["code"] = 1000000;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString()) };
        }

        private static JsonObject Device(string serial) => new() { ["deviceType"] = "INVERTER", ["deviceSn"] = serial, ["connectStatus"] = 1 };

        private JsonObject Telemetry(string serial)
        {
            var points = new JsonArray(new JsonObject { ["key"] = "batteryPower", ["value"] = Failure == "power" ? "80001" : "10000" });
            if (Failure != "missing-alarm") { points.Add(new JsonObject { ["key"] = "alarm", ["value"] = Failure == "alarm" ? "1" : "0" }); }
            if (Failure != "missing-soc") { points.Add(new JsonObject { ["key"] = "BMSSOC", ["value"] = Failure == "low-soc" ? "29" : "50" }); }
            points.Add(new JsonObject { ["key"] = "BMSVoltage", ["value"] = "290" });
            return new JsonObject { ["deviceSn"] = serial, ["collectionTime"] = Now.ToUnixTimeSeconds()
                + (Failure == "future" ? 1 : Failure == "stale" ? -601 : -10), ["dataList"] = points };
        }
    }
}
