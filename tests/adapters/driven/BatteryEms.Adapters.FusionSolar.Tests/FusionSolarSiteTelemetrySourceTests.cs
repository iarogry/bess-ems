using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BatteryEms.Application.Realtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BatteryEms.Adapters.FusionSolar.Tests;

public sealed class FusionSolarSiteTelemetrySourceTests
{
    [Fact]
    public async Task PollStationAsync_logs_in_reads_real_time_kpi_and_updates_site_store()
    {
        var options = new FusionSolarOptions
        {
            BaseUrl = "https://eu5.fusionsolar.huawei.com/thirdData",
            User = "user",
            Password = "password",
            StationCodes = "NE=12345",
            AssetId = "single-bess-1",
        };
        var handler = new MockHttpMessageHandler();
        handler.AddResponse("login", new { success = true }, setCookie: "XSRF-TOKEN=test-token; Path=/");
        handler.AddResponse("getStationRealKpi", new
        {
            success = true,
            data = new[]
            {
                new
                {
                    stationCode = "NE=12345",
                    collectTime = 1780588800000,
                    dataItemMap = new { active_power = 2.5 },
                },
                new
                {
                    stationCode = "NE=12345",
                    collectTime = 1780592400000,
                    dataItemMap = new { active_power = 3.75 },
                },
            },
        });
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
        };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("FusionSolar").Returns(httpClient);
        var store = new InMemorySiteTelemetryStore(TimeSpan.FromMinutes(10));
        var source = new FusionSolarSiteTelemetrySource(
            httpClientFactory,
            Options.Create(options),
            store,
            Substitute.For<ILogger<FusionSolarSiteTelemetrySource>>());

        var updated = await source.PollStationAsync(
            "NE=12345",
            new DateTimeOffset(2026, 6, 4, 16, 35, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(1, updated);
        var snapshot = store.GetLatest(
            "single-bess-1",
            new DateTimeOffset(2026, 6, 4, 16, 35, 0, TimeSpan.Zero));
        Assert.NotNull(snapshot);
        Assert.Equal(3.75, snapshot!.Telemetry.PvPowerKw);
        Assert.Null(snapshot.Telemetry.LoadPowerKw);
        Assert.Equal(new DateTimeOffset(2026, 6, 4, 16, 35, 0, TimeSpan.Zero), snapshot.Telemetry.Timestamp);

        var login = Assert.Single(handler.CapturedRequests, r => r.Path.EndsWith("login", StringComparison.Ordinal));
        Assert.DoesNotContain("XSRF-TOKEN", login.Headers, StringComparison.Ordinal);
        var kpi = Assert.Single(handler.CapturedRequests, r => r.Path.EndsWith("getStationRealKpi", StringComparison.Ordinal));
        Assert.Contains("XSRF-TOKEN: test-token", kpi.Headers, StringComparison.Ordinal);
        var body = JsonSerializer.Deserialize<JsonElement>(kpi.Body);
        Assert.Equal("NE=12345", body.GetProperty("stationCodes").GetString());
    }

    [Fact]
    public async Task PollStationAsync_reuses_authentication_and_isolates_station_failures()
    {
        var options = new FusionSolarOptions
        {
            BaseUrl = "https://eu5.fusionsolar.huawei.com/thirdData",
            User = "user",
            Password = "password",
            StationCodes = "NE=bad,NE=good",
        };
        var handler = new MockHttpMessageHandler();
        handler.AddResponse("login", new { success = true }, setCookie: "XSRF-TOKEN=test-token; Path=/");
        handler.AddResponse("getStationRealKpi", request =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(request.Body);
            var stationCode = body.GetProperty("stationCodes").GetString();
            return string.Equals(stationCode, "NE=bad", StringComparison.Ordinal)
                ? new
                {
                    success = false,
                    failCode = 20056,
                    message = (string?)null,
                }
                : new
                {
                    success = true,
                    failCode = 0,
                    data = new[]
                    {
                        new
                        {
                            stationCode = "NE=good",
                            collectTime = 1780592400000,
                            dataItemMap = new { active_power = 7.25 },
                        },
                    },
                };
        });
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
        };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("FusionSolar").Returns(httpClient);
        var store = new InMemorySiteTelemetryStore(TimeSpan.FromMinutes(10));
        var source = new FusionSolarSiteTelemetrySource(
            httpClientFactory,
            Options.Create(options),
            store,
            Substitute.For<ILogger<FusionSolarSiteTelemetrySource>>());

        var now = new DateTimeOffset(2026, 6, 4, 16, 35, 0, TimeSpan.Zero);
        var failed = await source.PollStationAsync("NE=bad", now, CancellationToken.None);
        var updated = await source.PollStationAsync("NE=good", now, CancellationToken.None);

        Assert.Equal(0, failed);
        Assert.Equal(1, updated);
        Assert.Null(store.GetLatest("NE=bad", new DateTimeOffset(2026, 6, 4, 16, 35, 0, TimeSpan.Zero)));
        var snapshot = store.GetLatest(
            "NE=good",
            new DateTimeOffset(2026, 6, 4, 16, 35, 0, TimeSpan.Zero));
        Assert.NotNull(snapshot);
        Assert.Equal(7.25, snapshot!.Telemetry.PvPowerKw);

        Assert.Equal(1, handler.CapturedRequests.Count(r => r.Path.EndsWith("login", StringComparison.Ordinal)));
        Assert.Equal(2, handler.CapturedRequests.Count(r => r.Path.EndsWith("getStationRealKpi", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PollStationAsync_accepts_wrapped_station_array_inside_data_object()
    {
        var options = new FusionSolarOptions
        {
            BaseUrl = "https://eu5.fusionsolar.huawei.com/thirdData",
            User = "user",
            Password = "password",
            StationCodes = "NE=12345",
            AssetId = "single-bess-1",
        };
        var handler = new MockHttpMessageHandler();
        handler.AddResponse("login", new { success = true }, setCookie: "XSRF-TOKEN=test-token; Path=/");
        handler.AddResponse("getStationRealKpi", new
        {
            success = true,
            data = new
            {
                list = new[]
                {
                    new
                    {
                        stationCode = "NE=12345",
                        collectTime = 1780592400000,
                        dataItemMap = new { active_power = 4.25 },
                    },
                },
            },
        });
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
        };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("FusionSolar").Returns(httpClient);
        var store = new InMemorySiteTelemetryStore(TimeSpan.FromMinutes(10));
        var source = new FusionSolarSiteTelemetrySource(
            httpClientFactory,
            Options.Create(options),
            store,
            Substitute.For<ILogger<FusionSolarSiteTelemetrySource>>());

        var updated = await source.PollStationAsync(
            "NE=12345",
            new DateTimeOffset(2026, 6, 4, 16, 35, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(1, updated);
        var snapshot = store.GetLatest(
            "single-bess-1",
            new DateTimeOffset(2026, 6, 4, 16, 35, 0, TimeSpan.Zero));
        Assert.NotNull(snapshot);
        Assert.Equal(4.25, snapshot!.Telemetry.PvPowerKw);
    }

    [Fact]
    public async Task PollStationAsync_returns_zero_when_real_time_endpoint_is_rate_limited()
    {
        var options = new FusionSolarOptions
        {
            BaseUrl = "https://eu5.fusionsolar.huawei.com/thirdData",
            User = "user",
            Password = "password",
            StationCodes = "NE=12345",
            AssetId = "single-bess-1",
        };
        var handler = new MockHttpMessageHandler();
        handler.AddResponse("login", new { success = true }, setCookie: "XSRF-TOKEN=test-token; Path=/");
        handler.AddResponse("getStationRealKpi", new
        {
            success = false,
            failCode = 407,
            data = "ACCESS_FREQUENCY_IS_TOO_HIGH",
        });
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
        };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("FusionSolar").Returns(httpClient);
        var store = new InMemorySiteTelemetryStore(TimeSpan.FromMinutes(10));
        var source = new FusionSolarSiteTelemetrySource(
            httpClientFactory,
            Options.Create(options),
            store,
            Substitute.For<ILogger<FusionSolarSiteTelemetrySource>>());

        var now = new DateTimeOffset(2026, 6, 4, 16, 35, 0, TimeSpan.Zero);
        var updated = await source.PollStationAsync("NE=12345", now, CancellationToken.None);

        Assert.Equal(0, updated);
        var snapshot = store.GetLatest("single-bess-1", now);
        Assert.Null(snapshot);
        Assert.Equal(1, handler.CapturedRequests.Count(r => r.Path.EndsWith("getStationRealKpi", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PollAsync_requests_all_stations_once_and_reuses_one_token()
    {
        var options = new FusionSolarOptions
        {
            BaseUrl = "https://eu5.fusionsolar.huawei.com/thirdData",
            User = "user",
            Password = "password",
            StationCodes = "NE=first,NE=second,NE=third",
        };
        var handler = new MockHttpMessageHandler();
        handler.AddResponse("login", new { success = true }, setCookie: "XSRF-TOKEN=test-token; Path=/");
        handler.AddResponse("getStationRealKpi", request =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(request.Body);
            var stationCodes = body.GetProperty("stationCodes").GetString()!.Split(',');
            return new
            {
                success = true,
                data = stationCodes.Select(stationCode => new
                    {
                        stationCode,
                        collectTime = 1780592400000,
                        dataItemMap = new { active_power = 1.0 },
                    }).ToArray(),
            };
        });
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
        };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("FusionSolar").Returns(httpClient);
        var source = new FusionSolarSiteTelemetrySource(
            httpClientFactory,
            Options.Create(options),
            new InMemorySiteTelemetryStore(TimeSpan.FromMinutes(10)),
            Substitute.For<ILogger<FusionSolarSiteTelemetrySource>>());

        var updated = await source.PollAsync(
            new DateTimeOffset(2026, 6, 4, 16, 35, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(3, updated);
        var request = Assert.Single(handler.CapturedRequests, r => r.Path.EndsWith("getStationRealKpi", StringComparison.Ordinal));
        Assert.Equal(
            "NE=first,NE=second,NE=third",
            JsonSerializer.Deserialize<JsonElement>(request.Body).GetProperty("stationCodes").GetString());
        Assert.Equal(1, handler.CapturedRequests.Count(r => r.Path.EndsWith("login", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PollStationAsync_renews_token_before_huawei_expiration()
    {
        var options = new FusionSolarOptions
        {
            BaseUrl = "https://eu5.fusionsolar.huawei.com/thirdData",
            User = "user",
            Password = "password",
            StationCodes = "NE=12345",
        };
        var handler = new MockHttpMessageHandler();
        handler.AddResponse("login", new { success = true }, setCookie: "XSRF-TOKEN=test-token; Path=/");
        handler.AddResponse("getStationRealKpi", new
        {
            success = true,
            data = new[]
            {
                new
                {
                    stationCode = "NE=12345",
                    collectTime = 1780592400000,
                    dataItemMap = new { active_power = 1.0 },
                },
            },
        });
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
        };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("FusionSolar").Returns(httpClient);
        var source = new FusionSolarSiteTelemetrySource(
            httpClientFactory,
            Options.Create(options),
            new InMemorySiteTelemetryStore(TimeSpan.FromMinutes(10)),
            Substitute.For<ILogger<FusionSolarSiteTelemetrySource>>());
        var firstPoll = new DateTimeOffset(2026, 6, 4, 16, 0, 0, TimeSpan.Zero);

        await source.PollStationAsync("NE=12345", firstPoll, CancellationToken.None);
        await source.PollStationAsync("NE=12345", firstPoll.AddMinutes(28), CancellationToken.None);
        await source.PollStationAsync("NE=12345", firstPoll.AddMinutes(29), CancellationToken.None);

        Assert.Equal(2, handler.CapturedRequests.Count(r => r.Path.EndsWith("login", StringComparison.Ordinal)));
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, QueuedResponse> _responses = new(StringComparer.Ordinal);

        public List<CapturedRequest> CapturedRequests { get; } = [];

        public void AddResponse(string pathSuffix, object response, string? setCookie = null)
        {
            _responses[pathSuffix] = new QueuedResponse(_ => response, setCookie);
        }

        public void AddResponse(string pathSuffix, Func<CapturedRequest, object> responseFactory, string? setCookie = null)
        {
            _responses[pathSuffix] = new QueuedResponse(responseFactory, setCookie);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            CapturedRequests.Add(new CapturedRequest(
                request.RequestUri?.AbsolutePath ?? string.Empty,
                string.Join(
                    "\n",
                    request.Headers.Select(header => $"{header.Key}: {string.Join(",", header.Value)}")),
                body));

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var match = _responses.Keys.FirstOrDefault(key => path.EndsWith(key, StringComparison.Ordinal));
            if (match is null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var queued = _responses[match];
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(queued.BodyFactory(CapturedRequests[^1])),
            };
            if (queued.SetCookie is not null)
            {
                response.Headers.TryAddWithoutValidation("Set-Cookie", queued.SetCookie);
            }

            return response;
        }

        private sealed record QueuedResponse(Func<CapturedRequest, object> BodyFactory, string? SetCookie);

        public sealed record CapturedRequest(string Path, string Headers, string Body);
    }
}
