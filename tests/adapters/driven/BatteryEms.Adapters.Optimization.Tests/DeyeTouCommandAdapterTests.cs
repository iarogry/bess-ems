using System.Net;
using System.Net.Http.Json;
using BatteryEms.Adapters.Optimization.Deye;
using Xunit;

namespace BatteryEms.Adapters.Optimization.Tests;

public sealed class DeyeTouCommandAdapterTests
{
    private static DeyeTouGuardState SafeGuards => new(true, true, true, 52, 20, 80);

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
