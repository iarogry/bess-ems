using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BatteryEms.Application.Realtime;
using BatteryEms.Application.Site;
using BatteryEms.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BatteryEms.Adapters.FusionSolar.Tests;

public sealed class FusionSolarSiteTelemetrySourceTests
{
    private static readonly DateTimeOffset MeasurementTime = DateTimeOffset.FromUnixTimeMilliseconds(1780592400000);

    [Theory]
    [InlineData(false, false, 12d, "Valid")]
    [InlineData(true, false, 12d, "Substituted")]
    [InlineData(false, true, null, "ProtocolError")]
    public async Task Device_power_is_summed_per_station_only_when_every_inverter_is_present(
        bool omitSampleTime, bool omitInverter, double? expected, string quality)
    {
        var handler = new MockHttpMessageHandler();
        handler.AddResponse("login", new { success = true }, setCookie: "XSRF-TOKEN=test-token; Path=/");
        handler.AddResponse("getDevList", new { success = true, data = new[]
        {
            new { id = 11L, devTypeId = 1, stationCode = "A" },
            new { id = 12L, devTypeId = 1, stationCode = "A" },
            new { id = 13L, devTypeId = 17, stationCode = "A" },
            new { id = 14L, devTypeId = 1, stationCode = "B" },
        } });
        var measurements = new List<Dictionary<string, object>>();
        foreach (var id in omitInverter ? new[] { 11L, 14L } : new[] { 11L, 12L, 14L })
        {
            var reading = new Dictionary<string, object>
            {
                ["devId"] = id, ["dataItemMap"] = new { active_power = id == 14 ? 1000 : 6, day_cap = 500 },
            };
            if (!omitSampleTime) { reading["collectTime"] = MeasurementTime.ToUnixTimeMilliseconds(); }
            measurements.Add(reading);
        }
        handler.AddResponse("getDevRealKpi", new { success = true, data = measurements });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/thirdData/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("FusionSolar").Returns(client);
        var store = new InMemorySiteTelemetryStore(TimeSpan.FromMinutes(10));
        var measurementsStore = new InMemorySiteMeasurementStore();
        using var source = new FusionSolarSiteTelemetrySource(factory,
            Options.Create(new FusionSolarOptions { StationCodes = "A,B", User = "user", Password = "password",
                StationSiteIds = { ["A"] = "site-a" } }),
            store, Substitute.For<ILogger<FusionSolarSiteTelemetrySource>>(), measurementsStore);
        await source.PollAsync(MeasurementTime, CancellationToken.None);
        await source.PollAsync(MeasurementTime.AddMinutes(5), CancellationToken.None);
        var snapshot = store.GetLatest("A", MeasurementTime.AddMinutes(5))!;
        Assert.Equal(expected, snapshot.Telemetry.PvPowerKw);
        Assert.Equal(quality, snapshot.Quality.Flag.ToString());
        await AssertPersistedAsync(measurementsStore, expected, omitInverter ? "source_error" : omitSampleTime ? "substituted" : "valid");
        Assert.Equal(1000, store.GetLatest("B", MeasurementTime.AddMinutes(5))!.Telemetry.PvPowerKw);
        Assert.Single(handler.CapturedRequests, request => request.Path.EndsWith("getDevList", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Path.EndsWith("getStationRealKpi", StringComparison.Ordinal));
        Assert.All(handler.CapturedRequests.Where(request => request.Path.EndsWith("getDevRealKpi", StringComparison.Ordinal)),
            request => Assert.DoesNotContain("13", request.Body, StringComparison.Ordinal));
    }

    private static async Task AssertPersistedAsync(ISiteMeasurementStore measurementsStore, double? expected, string quality)
    {
        var rows = await measurementsStore.QueryAsync(new SiteMeasurementQuery("site-a", MeasurementTime.AddSeconds(-1),
            MeasurementTime.AddMinutes(6), Source: "fusionsolar"), CancellationToken.None);
        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.Equal("A", row.InstrumentId);
            Assert.Equal("pv_power", row.Metric);
            Assert.Equal("kW", row.Unit);
            Assert.Equal(expected, row.Value);
            Assert.Equal(quality, row.Quality);
            row.EnsureValid();
        });
        Assert.NotEmpty(await measurementsStore.QueryAsync(new SiteMeasurementQuery("unassigned:fusionsolar:B",
            MeasurementTime.AddSeconds(-1), MeasurementTime.AddMinutes(6)), CancellationToken.None));
    }

    [Theory]
    [InlineData("inverterYield")]
    [InlineData("PVYield")]
    [InlineData("day_power")]
    [InlineData("inverter_power")]
    public async Task Energy_or_unverified_fields_are_never_used_as_live_power(string field)
    {
        var (source, store) = CreateSource(new FusionSolarOptions { UseDeviceTelemetry = false, StationCodes = "A", User = "user", Password = "password" },
            new[] { new { stationCode = "A", collectTime = MeasurementTime.ToUnixTimeMilliseconds(), dataItemMap = new Dictionary<string, double?> { [field] = 120 } } });
        using (source)
        {
            Assert.Equal(0, await source.PollAsync(MeasurementTime.AddMinutes(1), CancellationToken.None));
            var snapshot = store.GetLatest("A", MeasurementTime.AddMinutes(1));
            Assert.NotNull(snapshot);
            Assert.Null(snapshot.Telemetry.PvPowerKw);
            Assert.Equal(DataQualityState.ProtocolError, snapshot.Quality.Flag);
        }
    }

    [Theory]
    [InlineData(-20, "Stale")]
    [InlineData(2, "ProtocolError")]
    public async Task Vendor_time_is_checked_instead_of_replaced_by_poll_time(int offsetMinutes, string quality)
    {
        var measured = MeasurementTime.AddMinutes(offsetMinutes);
        var (source, store) = CreateSource(new FusionSolarOptions { UseDeviceTelemetry = false, StationCodes = "A", User = "user", Password = "password" },
            new[] { new { stationCode = "A", collectTime = measured.ToUnixTimeMilliseconds(), dataItemMap = new { active_power = 5 } } });
        using (source)
        {
            Assert.Equal(0, await source.PollAsync(MeasurementTime, CancellationToken.None));
            Assert.Equal(quality, store.GetLatest("A", MeasurementTime)!.Quality.Flag.ToString());
        }
    }

    [Fact]
    public async Task Account_stations_are_not_implicitly_combined_into_battery_or_site()
    {
        var options = new FusionSolarOptions { UseDeviceTelemetry = false, StationCodes = "A,B", AssetId = "site-a", User = "user", Password = "password" };
        var (source, store) = CreateSource(options, new[]
        {
            new { stationCode = "A", collectTime = MeasurementTime.ToUnixTimeMilliseconds(), dataItemMap = new { active_power = 5 } },
            new { stationCode = "B", collectTime = MeasurementTime.ToUnixTimeMilliseconds(), dataItemMap = new { active_power = 7 } },
        });
        using (source)
        {
            Assert.Equal(2, await source.PollAsync(MeasurementTime, CancellationToken.None));
            Assert.Null(store.GetLatest("site-a", MeasurementTime));
            Assert.Equal(5, store.GetLatest("A", MeasurementTime)!.Telemetry.PvPowerKw);
            Assert.Equal(7, store.GetLatest("B", MeasurementTime)!.Telemetry.PvPowerKw);
        }
    }

    [Fact]
    public async Task Site_sum_uses_only_explicit_members_deduplicates_and_keeps_oldest_measurement_time()
    {
        var options = new FusionSolarOptions { UseDeviceTelemetry = false, StationCodes = "A,B,C,A", SiteStationCodes = "A,B,A", AssetId = "site-a", User = "user", Password = "password" };
        var (source, store) = CreateSource(options, new[]
        {
            new { stationCode = "A", collectTime = MeasurementTime.AddMinutes(-1).ToUnixTimeMilliseconds(), dataItemMap = new { active_power = 5 } },
            new { stationCode = "A", collectTime = MeasurementTime.AddMinutes(-2).ToUnixTimeMilliseconds(), dataItemMap = new { active_power = 99 } },
            new { stationCode = "B", collectTime = MeasurementTime.ToUnixTimeMilliseconds(), dataItemMap = new { active_power = 0 } },
            new { stationCode = "C", collectTime = MeasurementTime.ToUnixTimeMilliseconds(), dataItemMap = new { active_power = 1000 } },
        });
        using (source)
        {
            Assert.Equal(3, await source.PollAsync(MeasurementTime, CancellationToken.None));
            var snapshot = store.GetLatest("site-a", MeasurementTime)!;
            Assert.Equal(5, snapshot.Telemetry.PvPowerKw);
            Assert.Equal(MeasurementTime.AddMinutes(-1), snapshot.Telemetry.Timestamp);
            Assert.Equal(MeasurementTime, snapshot.ReceivedAt);
            Assert.Equal(DataQualityState.Valid, snapshot.Quality.Flag);
        }
    }

    [Fact]
    public async Task Missing_site_member_invalidates_previous_total_without_losing_healthy_station()
    {
        var options = new FusionSolarOptions { UseDeviceTelemetry = false, StationCodes = "A,B", SiteStationCodes = "A,B", AssetId = "site-a", User = "user", Password = "password" };
        var (source, store) = CreateSource(options,
            new[] { new { stationCode = "A", collectTime = MeasurementTime.ToUnixTimeMilliseconds(), dataItemMap = new { active_power = 5 } } });
        store.Update(new SiteTelemetry(MeasurementTime, "site-a", 123, null, null, null, DataQuality.Valid), MeasurementTime);
        using (source)
        {
            Assert.Equal(1, await source.PollAsync(MeasurementTime, CancellationToken.None));
            var snapshot = store.GetLatest("site-a", MeasurementTime)!;
            Assert.Null(snapshot.Telemetry.PvPowerKw);
            Assert.Equal(DataQualityState.ProtocolError, snapshot.Quality.Flag);
            Assert.Equal(5, store.GetLatest("A", MeasurementTime)!.Telemetry.PvPowerKw);
        }
    }

    [Fact]
    public void Registration_does_not_infer_site_membership_from_single_battery()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISiteTelemetryStore>(new InMemorySiteTelemetryStore(TimeSpan.FromMinutes(10)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FusionSolar:User"] = "user", ["FusionSolar:Password"] = "password", ["FusionSolar:StationCodes"] = "A,B",
            ["Dashboard:Sites:0:SiteId"] = "site-a",
            ["Dashboard:Sites:0:Sources:0:TelemetryId"] = "A",
            ["Dashboard:Sites:0:Sources:0:Kind"] = "pv",
        }).Build();
        services.AddFusionSolarSiteTelemetry(configuration, "battery-a");
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FusionSolarOptions>>().Value;
        Assert.Null(options.AssetId);
        Assert.Empty(options.ParsedSiteStationCodes);
        Assert.Equal("site-a", options.StationSiteIds["A"]);
    }

    [Fact]
    public async Task Persistence_failure_does_not_discard_live_snapshot()
    {
        var measurements = Substitute.For<ISiteMeasurementStore>();
        measurements.AppendAsync(Arg.Any<IReadOnlyList<SiteMeasurementReading>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Database unavailable")));
        var (source, store) = CreateSource(new FusionSolarOptions { UseDeviceTelemetry = false, StationCodes = "A" },
            new[] { new { stationCode = "A", collectTime = MeasurementTime.ToUnixTimeMilliseconds(), dataItemMap = new { active_power = 12 } } }, measurements);
        using (source)
        {
            Assert.Equal(1, await source.PollAsync(MeasurementTime, CancellationToken.None));
            Assert.Equal(12, store.GetLatest("A", MeasurementTime)!.Telemetry.PvPowerKw);
            await measurements.Received(1).AppendAsync(Arg.Any<IReadOnlyList<SiteMeasurementReading>>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task Source_failure_persists_null_status_instead_of_zero_power()
    {
        var measurements = new InMemorySiteMeasurementStore();
        var (source, _) = CreateSource(new FusionSolarOptions { UseDeviceTelemetry = false, StationCodes = "A" },
            new { unexpected = true }, measurements);
        using (source)
        {
            Assert.Equal(0, await source.PollAsync(MeasurementTime, CancellationToken.None));
            var rows = await measurements.QueryAsync(new SiteMeasurementQuery("unassigned:fusionsolar:A",
                MeasurementTime.AddSeconds(-1), MeasurementTime.AddSeconds(1)), CancellationToken.None);
            var row = Assert.Single(rows);
            Assert.Null(row.Value);
            Assert.Equal("source_error", row.Quality);
        }
    }

    private static (FusionSolarSiteTelemetrySource Source, InMemorySiteTelemetryStore Store) CreateSource(FusionSolarOptions options, object readings,
        ISiteMeasurementStore? measurements = null)
    {
        var handler = new MockHttpMessageHandler();
        handler.AddResponse("login", new { success = true }, setCookie: "XSRF-TOKEN=test-token; Path=/");
        handler.AddResponse("getStationRealKpi", new { success = true, data = readings });
        var client = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("FusionSolar").Returns(client);
        var store = new InMemorySiteTelemetryStore(TimeSpan.FromMinutes(10));
        return (new FusionSolarSiteTelemetrySource(factory, Options.Create(options), store,
            Substitute.For<ILogger<FusionSolarSiteTelemetrySource>>(), measurements), store);
    }

    [Fact]
    public async Task PollStationAsync_logs_in_reads_real_time_kpi_and_updates_site_store()
    {
        var options = new FusionSolarOptions
        {
            UseDeviceTelemetry = false,
            BaseUrl = "https://eu5.fusionsolar.huawei.com/thirdData",
            User = "user",
            Password = "password",
            StationCodes = "NE=12345",
            AssetId = "single-bess-1",
            SiteStationCodes = "NE=12345",
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
            MeasurementTime.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(1, updated);
        var snapshot = store.GetLatest(
            "single-bess-1",
            MeasurementTime.AddMinutes(1));
        Assert.NotNull(snapshot);
        Assert.Equal(3.75, snapshot!.Telemetry.PvPowerKw);
        Assert.Null(snapshot.Telemetry.LoadPowerKw);
        Assert.Equal(MeasurementTime, snapshot.Telemetry.Timestamp);

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
            UseDeviceTelemetry = false,
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

        var now = MeasurementTime.AddMinutes(1);
        var failed = await source.PollStationAsync("NE=bad", now, CancellationToken.None);
        var updated = await source.PollStationAsync("NE=good", now, CancellationToken.None);

        Assert.Equal(0, failed);
        Assert.Equal(1, updated);
        Assert.Null(store.GetLatest("NE=bad", MeasurementTime.AddMinutes(1)));
        var snapshot = store.GetLatest(
            "NE=good",
            MeasurementTime.AddMinutes(1));
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
            UseDeviceTelemetry = false,
            BaseUrl = "https://eu5.fusionsolar.huawei.com/thirdData",
            User = "user",
            Password = "password",
            StationCodes = "NE=12345",
            AssetId = "single-bess-1",
            SiteStationCodes = "NE=12345",
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
            MeasurementTime.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(1, updated);
        var snapshot = store.GetLatest(
            "single-bess-1",
            MeasurementTime.AddMinutes(1));
        Assert.NotNull(snapshot);
        Assert.Equal(4.25, snapshot!.Telemetry.PvPowerKw);
    }

    [Fact]
    public async Task PollStationAsync_returns_zero_when_real_time_endpoint_is_rate_limited()
    {
        var options = new FusionSolarOptions
        {
            UseDeviceTelemetry = false,
            BaseUrl = "https://eu5.fusionsolar.huawei.com/thirdData",
            User = "user",
            Password = "password",
            StationCodes = "NE=12345",
            AssetId = "single-bess-1",
            SiteStationCodes = "NE=12345",
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

        var now = MeasurementTime.AddMinutes(1);
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
            UseDeviceTelemetry = false,
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
            MeasurementTime.AddMinutes(1),
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
            UseDeviceTelemetry = false,
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
