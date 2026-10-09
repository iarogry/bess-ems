using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatteryEms.Application.Realtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BatteryEms.Adapters.FusionSolar.Tests;

public sealed class FusionSolarLiveScratchTests
{
    private static readonly JsonSerializerOptions OutputJsonOptions = new() { WriteIndented = true };

    [Fact]
    public async Task PollStationAsync_reads_four_live_stations_from_env()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("BESS_RUN_LIVE_FUSIONSOLAR_TESTS"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        LoadDotEnv();

        var options = new FusionSolarOptions
        {
            BaseUrl = Required("FusionSolar__BaseUrl"),
            User = Required("FusionSolar__User"),
            Password = Required("FusionSolar__Password"),
            StationCodes = Required("FusionSolar__StationCodes"),
            AssetId = null,
            HttpTimeoutSeconds = 30,
        };
        var stationCodes = options.ParsedStationCodes;
        Assert.Equal(4, stationCodes.Count);

        var now = DateTimeOffset.Now;
        var rawResults = await PollRawByStationAsync(options, stationCodes, now, CancellationToken.None);
        var rawPath = WriteRawResult(now, rawResults);

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds),
        };
        var store = new InMemorySiteTelemetryStore(TimeSpan.FromDays(2));
        var source = new FusionSolarSiteTelemetrySource(
            new SingleClientFactory(httpClient),
            Options.Create(options),
            store,
            NullLogger<FusionSolarSiteTelemetrySource>.Instance);

        var updated = 0;
        foreach (var stationCode in stationCodes)
        {
            updated += await source
                .PollStationAsync(stationCode, now, CancellationToken.None)
                .ConfigureAwait(true);
        }

        var stations = stationCodes
            .Select(code => new
            {
                station_code = code,
                snapshot = store.GetLatest(code, now),
            })
            .Select(item => new
            {
                item.station_code,
                found = item.snapshot is not null,
                timestamp = item.snapshot?.Telemetry.Timestamp,
                pv_power_kw = item.snapshot?.Telemetry.PvPowerKw,
                data_quality = item.snapshot?.Quality.Flag.ToString(),
                data_quality_reason = item.snapshot?.Quality.Reason,
            })
            .ToArray();

        Directory.CreateDirectory("tmp");
        var output = new
        {
            polled_at = now,
            endpoint = "/getKpiStationHour",
            configured_station_count = stationCodes.Count,
            updated_station_count = updated,
            raw_result_file = rawPath,
            stations,
        };
        var outputPath = Path.Combine(
            "tmp",
            $"fusionsolar-live-{now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(output, OutputJsonOptions),
            CancellationToken.None);

        var rawSuccessfulStations = rawResults.Count(result =>
        {
            var json = JsonSerializer.SerializeToElement(result);
            return json.GetProperty("success").GetBoolean()
                && json.GetProperty("data_count").GetInt32() > 0;
        });
        Assert.Equal(rawSuccessfulStations, updated);
        Assert.Equal(updated, stations.Count(station => station.found));
    }

    private static async Task<IReadOnlyList<object>> PollRawByStationAsync(
        FusionSolarOptions options,
        IReadOnlyList<string> stationCodes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds),
        };
        using var loginResponse = await httpClient.PostAsJsonAsync(
            "login",
            new JsonObject
            {
                ["userName"] = options.User,
                ["systemCode"] = options.Password,
            },
            cancellationToken);
        loginResponse.EnsureSuccessStatusCode();

        var xsrfToken = TryExtractXsrfToken(loginResponse);
        if (!string.IsNullOrWhiteSpace(xsrfToken))
        {
            httpClient.DefaultRequestHeaders.Remove("XSRF-TOKEN");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("XSRF-TOKEN", xsrfToken);
        }

        var collectTime = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var results = new List<object>(stationCodes.Count);
        foreach (var stationCode in stationCodes)
        {
            using var response = await httpClient.PostAsJsonAsync(
                "getKpiStationHour",
                new JsonObject
                {
                    ["stationCodes"] = stationCode,
                    ["collectTime"] = collectTime,
                },
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            var data = json?["data"]?.AsArray();
            var latest = data?
                .Select(node => node?.AsObject())
                .Where(node => node is not null)
                .OrderByDescending(node => (long?)node?["collectTime"])
                .FirstOrDefault(node => node?["dataItemMap"]?["inverterYield"] is not null);
            var latestCollectTime = (long?)latest?["collectTime"];

            results.Add(new
            {
                station_code = stationCode,
                success = (bool?)json?["success"],
                fail_code = (int?)json?["failCode"],
                message = (string?)json?["message"],
                data_count = data?.Count ?? 0,
                latest_collect_utc = latestCollectTime is null
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(latestCollectTime.Value).ToString("O"),
                inverter_yield = (double?)latest?["dataItemMap"]?["inverterYield"],
            });
        }

        return results;
    }

    private static string WriteRawResult(DateTimeOffset now, IReadOnlyList<object> results)
    {
        Directory.CreateDirectory("tmp");
        var outputPath = Path.Combine(
            "tmp",
            $"fusionsolar-raw-{now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(
                new
                {
                    polled_at = now,
                    endpoint = "/getKpiStationHour",
                    station_count = results.Count,
                    results,
                },
                OutputJsonOptions));
        return outputPath;
    }

    private static string Required(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{key} is required for FusionSolar live scratch test.");
        }

        return value;
    }

    private static void LoadDotEnv()
    {
        var path = FindDotEnv();
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string? TryExtractXsrfToken(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        foreach (var cookie in cookies)
        {
            foreach (var part in cookie.Split(';', StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal))
                {
                    return part["XSRF-TOKEN=".Length..];
                }
            }
        }

        return null;
    }

    private static string FindDotEnv()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), ".env");
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
