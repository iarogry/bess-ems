using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BatteryEms.Adapters.Optimization.Deye;
using Xunit;

namespace BatteryEms.Adapters.Optimization.Tests;

public sealed class DeyeTouCommandAdapterTests
{
    private static DeyeTouGuardState SafeGuards => new(true, true, true, 52, 30, 80);

    [Fact]
    public async Task Write_is_fail_closed_when_feature_flag_is_off()
    {
        var handler = new RecordingHandler();
        var sut = new DeyeTouCommandAdapter(new HttpClient(handler), new Uri("https://deye.test/"), "MASTER", false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateAsync("MASTER", ValidIntervals(), SafeGuards, CancellationToken.None));
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Write_rejects_non_contiguous_or_malformed_schedule_before_network_call()
    {
        var handler = new RecordingHandler();
        var sut = new DeyeTouCommandAdapter(new HttpClient(handler), new Uri("https://deye.test/"), "MASTER", true);
        var list = ValidIntervals().ToList();
        list[1] = list[1] with { Time = "00:00" };

        await Assert.ThrowsAsync<ArgumentException>(() => sut.UpdateAsync("MASTER", list, SafeGuards, CancellationToken.None));
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Write_targets_only_master_and_sends_tou_items()
    {
        var handler = new RecordingHandler();
        var sut = new DeyeTouCommandAdapter(new HttpClient(handler), new Uri("https://deye.test/"), "MASTER", true);

        var result = await sut.UpdateAsync("MASTER", ValidIntervals(), SafeGuards, CancellationToken.None);

        Assert.Equal("\"ok\"", result);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Contains("order/sys/tou/update", handler.Request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("timeUseSettingItems", handler.Body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(handler.Body);
        var items = json.RootElement.GetProperty("timeUseSettingItems").EnumerateArray().ToArray();
        Assert.Equal(["04:00", "08:00", "12:00", "18:00", "20:00", "00:00"],
            items.Select(item => item.GetProperty("time").GetString()!).ToArray());
        Assert.Equal(80, items[0].GetProperty("soc").GetInt32());
        Assert.Equal(40, items[1].GetProperty("soc").GetInt32());
    }

    [Fact]
    public void Device_payload_preserves_every_start_time_settings_pair_and_input_order()
    {
        var source = ValidIntervals();
        var original = source.ToArray();
        var payload = DeyeTouCommandAdapter.ToDevicePayload(source);
        Assert.Equal(["04:00", "08:00", "12:00", "18:00", "20:00", "00:00"], payload.Select(item => item.Time).ToArray());
        foreach (var interval in source)
        {
            Assert.Equal(interval, Assert.Single(payload, item => item.Time == interval.Time));
        }
        Assert.Equal(original, source);
    }

    [Fact]
    public void Z4_keeps_idle_at_18_and_discharge_at_19_without_boundary_shift()
    {
        var source = new DeyeTouInterval[]
        {
            new("00:00", true, false, false, 80000, 30, 290),
            new("18:00", true, false, false, 0, 100, 290),
            new("19:00", true, false, true, 65650, 30, 290),
            new("20:00", true, false, true, 80000, 30, 290),
            new("22:00", true, false, false, 0, 30, 290),
            new("23:00", true, false, false, 0, 30, 290),
        };
        var payload = DeyeTouCommandAdapter.ToDevicePayload(source);
        Assert.Equal(["18:00", "19:00", "20:00", "22:00", "23:00", "00:00"], payload.Select(item => item.Time).ToArray());
        Assert.Equal((0, 100, false), (payload[0].Power, payload[0].Soc, payload[0].EnableSell));
        Assert.Equal((65650, 30, true), (payload[1].Power, payload[1].Soc, payload[1].EnableSell));
        Assert.Equal((80000, 30, false), (payload[5].Power, payload[5].Soc, payload[5].EnableSell));
    }

    [Fact]
    public async Task Write_omits_only_enable_sell_when_master_does_not_support_it()
    {
        var handler = new RecordingHandler();
        var sut = new DeyeTouCommandAdapter(new HttpClient(handler), new Uri("https://deye.test/"), "MASTER", true, false);
        var source = ValidIntervals();
        await sut.UpdateAsync("MASTER", source, SafeGuards, CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body);
        foreach (var item in json.RootElement.GetProperty("timeUseSettingItems").EnumerateArray())
        {
            Assert.False(item.TryGetProperty("enableSell", out _));
            var original = Assert.Single(source, interval => interval.Time == item.GetProperty("time").GetString());
            Assert.Equal(original.Power, item.GetProperty("power").GetInt32());
            Assert.Equal(original.Soc, item.GetProperty("soc").GetInt32());
            Assert.Equal(original.Voltage, item.GetProperty("voltage").GetInt32());
            Assert.Equal(original.EnableGeneration, item.GetProperty("enableGeneration").GetBoolean());
            Assert.Equal(original.EnableGridCharge, item.GetProperty("enableGridCharge").GetBoolean());
        }
    }

    [Theory]
    [InlineData(65650, 65659, true)]
    [InlineData(65659, 65659, true)]
    [InlineData(65660, 65659, false)]
    [InlineData(65649, 65659, false)]
    [InlineData(-10, -10, false)]
    public void Readback_power_allows_only_exact_or_downward_ten_watt_quantization(int observed, int requested, bool matches)
    {
        Assert.Equal(matches, DeyeTouCommandAdapter.PowerMatches(observed, requested));
    }

    [Theory]
    [InlineData(double.NaN, 30, 80)]
    [InlineData(double.PositiveInfinity, 30, 80)]
    [InlineData(101, 30, 80)]
    [InlineData(52, double.NaN, 80)]
    [InlineData(52, 20, 80)]
    [InlineData(52, 30, double.NaN)]
    [InlineData(52, 30, double.PositiveInfinity)]
    [InlineData(52, 30, 161)]
    public async Task Invalid_numeric_guards_block_update_and_switch_before_network(double soc, double reserve, double power)
    {
        var handler = new RecordingHandler();
        var sut = new DeyeTouCommandAdapter(new HttpClient(handler), new Uri("https://deye.test/"), "MASTER", true);
        var guards = SafeGuards with { BmsSocPercent = soc, UpsReserveSocPercent = reserve, RequestedPowerKw = power };
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateAsync("MASTER", ValidIntervals(), guards, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SwitchAsync("MASTER", true, guards, CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Null_interval_blocks_before_network()
    {
        var handler = new RecordingHandler();
        var sut = new DeyeTouCommandAdapter(new HttpClient(handler), new Uri("https://deye.test/"), "MASTER", true);
        var intervals = ValidIntervals().ToArray();
        intervals[1] = null!;
        await Assert.ThrowsAsync<ArgumentException>(() => sut.UpdateAsync("MASTER", intervals, SafeGuards, CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(-1, 30, 290)]
    [InlineData(80001, 30, 290)]
    [InlineData(80000, 29, 290)]
    [InlineData(80000, 101, 290)]
    [InlineData(80000, 30, -1)]
    public async Task Invalid_master_settings_block_before_network(int power, int soc, int voltage)
    {
        var handler = new RecordingHandler();
        var sut = new DeyeTouCommandAdapter(new HttpClient(handler), new Uri("https://deye.test/"), "MASTER", true);
        var intervals = ValidIntervals().ToArray();
        intervals[1] = intervals[1] with { Power = power, Soc = soc, Voltage = voltage };
        await Assert.ThrowsAsync<ArgumentException>(() => sut.UpdateAsync("MASTER", intervals, SafeGuards, CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Polling_returns_only_after_vendor_reports_success()
    {
        var handler = new RecordingHandler(
            "{\"status\":100,\"success\":true}",
            "{\"status\":666,\"success\":true,\"msg\":\"success\"}");
        var sut = new DeyeTouCommandAdapter(new HttpClient(handler), new Uri("https://deye.test/"), "MASTER", true);

        var result = await sut.WaitForResultAsync("123", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(666, result.Status);
        Assert.Equal(2, handler.CallCount);
        Assert.Contains("order/123", handler.Request!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    private static IReadOnlyList<DeyeTouInterval> ValidIntervals() =>
    [
        new("00:00", true, false, false, 0, 52, 0),
        new("04:00", true, true, false, 80, 80, 0),
        new("08:00", true, false, false, 0, 40, 0),
        new("12:00", true, false, true, 0, 40, 0),
        new("18:00", true, false, false, 0, 30, 0),
        new("20:00", true, false, false, 0, 30, 0)
    ];

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public RecordingHandler(params string[] responses) => _responses = new Queue<string>(responses.Length == 0 ? ["\"ok\""] : responses);
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            CallCount++;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var body = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
