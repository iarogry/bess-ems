using System.Net;
using System.Net.Http.Json;
using BatteryEms.Application.Site;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BatteryEms.Adapters.Askue.Tests;

public sealed class AskueSiteConsumptionCollectorTests
{
    [Fact]
    public async Task CollectAsync_fetches_profiles_preserves_server_intervals_and_stores_readings()
    {
        var options = new AskueOptions
        {
            SiteId = "site-1",
            BaseUrl = "http://askue.net/api/askue/v1/json/",
            Username = "user",
            Password = "password",
            PointIds = "101",
            TimeZoneId = "UTC",
            PeriodSeconds = 3600,
        };
        var handler = new MockHttpMessageHandler();
        handler.AddResponse("/points", new[]
        {
            new { id = 101, name = "Main meter", scale = 0.001 },
            new { id = 101, name = "Main meter duplicate", scale = 0.001 },
            new { id = 102, name = "Other meter", scale = 1.0 },
        });
        var dayStart = new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        handler.AddResponse("/point/101/profile/1", new[]
        {
            new { step = 1800, date = dayStart + 900, value = 1250.0 },
            new { step = 1800, date = dayStart + 1800, value = 2750.0 },
            new { step = 3600, date = dayStart + 3700, value = 5000.0 },
        });
        handler.AddResponse("/point/101/profile/2", new[]
        {
            new { step = 1800, date = dayStart + 900, value = 500.0 },
        });
        handler.AddResponse("/point/101/profile/3", Array.Empty<object>());
        handler.AddResponse("/point/101/profile/4", new[]
        {
            new { step = 3600, date = dayStart + 3700, value = 100.0 },
        });
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl),
        };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Askue").Returns(httpClient);
        var store = new InMemorySiteConsumptionStore();
        var collector = new AskueSiteConsumptionCollector(
            factory,
            Options.Create(options),
            store,
            Substitute.For<ILogger<AskueSiteConsumptionCollector>>());

        var count = await collector.CollectAsync(new DateOnly(2026, 6, 5), CancellationToken.None);

        Assert.Equal(3, count);
        var readings = await store.QueryAsync(
            new SiteConsumptionQuery(
                "site-1",
                new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 5, 3, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);
        Assert.Collection(
            readings,
            first =>
            {
                Assert.Equal(new DateTimeOffset(2026, 6, 5, 0, 15, 0, TimeSpan.Zero), first.Timestamp);
                Assert.Equal("101", first.PointId);
                Assert.Equal(1800, first.IntervalSeconds);
                Assert.Equal(1250.0, first.Apoz);
                Assert.Equal(500.0, first.Aneg);
                Assert.Null(first.Ppoz);
                Assert.Null(first.Pneg);
                Assert.Contains("\"scale\":0.001", first.MetadataJson, StringComparison.Ordinal);
            },
            second =>
            {
                Assert.Equal(new DateTimeOffset(2026, 6, 5, 0, 30, 0, TimeSpan.Zero), second.Timestamp);
                Assert.Equal(1800, second.IntervalSeconds);
                Assert.Equal(2750.0, second.Apoz);
                Assert.Null(second.Aneg);
                Assert.Contains("\"scale\":0.001", second.MetadataJson, StringComparison.Ordinal);
            },
            third =>
            {
                Assert.Equal(new DateTimeOffset(2026, 6, 5, 1, 1, 40, TimeSpan.Zero), third.Timestamp);
                Assert.Equal(3600, third.IntervalSeconds);
                Assert.Equal("101", third.PointId);
                Assert.Equal(5000.0, third.Apoz);
                Assert.Equal(100.0, third.Pneg);
                Assert.Contains("\"scale\":0.001", third.MetadataJson, StringComparison.Ordinal);
            });

        Assert.All(handler.CapturedRequests, request => Assert.Contains("Authorization: Basic", request.Headers, StringComparison.Ordinal));
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Path.Contains("/point/102/", StringComparison.Ordinal));
        Assert.Equal(4, handler.CapturedRequests.Count(request => request.Path.Contains("/point/101/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CollectAsync_queries_askue_by_configured_local_day_bounds()
    {
        var options = new AskueOptions
        {
            SiteId = "site-1",
            BaseUrl = "http://askue.net/api/askue/v1/json/",
            Username = "user",
            Password = "password",
            PointIds = "101",
            TimeZoneId = "Europe/Kyiv",
        };
        var handler = new MockHttpMessageHandler();
        handler.AddResponse("/points", new[] { new { id = 101, name = "Main meter" } });
        handler.AddResponse("/point/101/profile/1", Array.Empty<object>());
        handler.AddResponse("/point/101/profile/2", Array.Empty<object>());
        handler.AddResponse("/point/101/profile/3", Array.Empty<object>());
        handler.AddResponse("/point/101/profile/4", Array.Empty<object>());
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl),
        };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Askue").Returns(httpClient);
        var collector = new AskueSiteConsumptionCollector(
            factory,
            Options.Create(options),
            new InMemorySiteConsumptionStore(),
            Substitute.For<ILogger<AskueSiteConsumptionCollector>>());

        await collector.CollectAsync(new DateOnly(2026, 6, 9), CancellationToken.None);

        var localStart = DateTime.SpecifyKind(new DateTime(2026, 6, 9, 0, 0, 0), DateTimeKind.Unspecified);
        var localEnd = localStart.AddDays(1);
        var timeZone = ResolveTestTimeZone(options.TimeZoneId);
        var start = new DateTimeOffset(localStart, timeZone.GetUtcOffset(localStart)).ToUnixTimeSeconds();
        var end = new DateTimeOffset(localEnd, timeZone.GetUtcOffset(localEnd)).ToUnixTimeSeconds();
        var expectedQuery = $"b={start}&e={end}";
        Assert.All(
            handler.CapturedRequests.Where(request => request.Path.Contains("/point/101/profile/", StringComparison.Ordinal)),
            request => Assert.Contains(expectedQuery, request.Query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LivePollTest()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("BESS_RUN_LIVE_ASKUE_TESTS"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        await AskueLivePoll.RunAsync();
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, object> _responses = new(StringComparer.Ordinal);

        public List<CapturedRequest> CapturedRequests { get; } = [];

        public void AddResponse(string pathSuffix, object response)
        {
            _responses[pathSuffix] = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var query = request.RequestUri?.Query.TrimStart('?') ?? string.Empty;
            CapturedRequests.Add(new CapturedRequest(
                path,
                query,
                string.Join(
                    "\n",
                    request.Headers.Select(header => $"{header.Key}: {string.Join(",", header.Value)}"))));

            var match = _responses.Keys.FirstOrDefault(key => path.EndsWith(key, StringComparison.Ordinal));
            if (match is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(_responses[match]),
            });
        }

        public sealed record CapturedRequest(string Path, string Query, string Headers);
    }

    private static TimeZoneInfo ResolveTestTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            if (string.Equals(timeZoneId, "Europe/Kyiv", StringComparison.Ordinal))
            {
                return CreateKyivFallbackTimeZone();
            }

            throw;
        }
    }

    private static TimeZoneInfo CreateKyivFallbackTimeZone()
    {
        var daylightDelta = TimeSpan.FromHours(1);
        var dstStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 3, 0, 0),
            3,
            5,
            DayOfWeek.Sunday);
        var dstEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 4, 0, 0),
            10,
            5,
            DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            daylightDelta,
            dstStart,
            dstEnd);

        return TimeZoneInfo.CreateCustomTimeZone(
            "Europe/Kyiv",
            TimeSpan.FromHours(2),
            "(UTC+02:00) Kyiv",
            "EET",
            "EEST",
            [rule]);
    }
}
