using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using BatteryEms.Adapters.Entsoe;
using BatteryEms.Adapters.Optimization.OrTools;
using BatteryEms.Application.Markets;
using BatteryEms.Application.Optimization;
using BatteryEms.Application.Time;
using BatteryEms.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BatteryEms.Adapters.Optimization.Tests;

public sealed class TomorrowEmsScratchTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    [SuppressMessage("Maintainability", "CA1506", Justification = "Scratch integration harness deliberately composes the full flow in one place.")]
    public async Task Run_battery_ems_for_tomorrow_with_live_entsoe_prices()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("BESS_RUN_LIVE_ENTSOE_TESTS"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var root = FindRepoRoot();
        var dotEnvPath = Path.Combine(root, ".env");
        if (!File.Exists(dotEnvPath))
        {
            // This scratch harness depends on operator-provided live credentials and
            // should not fail containerized production builds when they are absent.
            return;
        }

        var env = ReadDotEnv(dotEnvPath);
        var kyivOffset = TimeSpan.FromHours(3);
        var tomorrow = DateTimeOffset.UtcNow.ToOffset(kyivOffset).Date.AddDays(1);
        var horizonStart = new DateTimeOffset(tomorrow.Year, tomorrow.Month, tomorrow.Day, 0, 0, 0, kyivOffset);
        var horizonEnd = horizonStart.AddDays(1);
        var timeStep = TimeSpan.FromHours(1);
        var entsoeOptions = new EntsoePriceSeriesOptions
        {
            BaseUrl = new Uri(Required(env, "Bess__EntsoeApiBaseUrl")),
            SecurityToken = Required(env, "Bess__EntsoeApiToken"),
            DomainCode = Required(env, "Bess__EntsoeDomainCode"),
            Product = EntsoePriceSeriesSource.RdnProduct,
            PriceKind = EntsoePriceSeriesSource.EnergyPriceKind,
            Source = EntsoePriceSeriesSource.SourceId,
            Unit = "UAH/MWh",
            RequestTimeout = TimeSpan.FromSeconds(60),
        };

        using var http = new HttpClient();
        var priceSource = new EntsoePriceSeriesSource(http, entsoeOptions);
        var prices = await priceSource.LoadAsync(
            new PriceSeriesRequest(
                entsoeOptions.DomainCode,
                EntsoePriceSeriesSource.RdnProduct,
                EntsoePriceSeriesSource.EnergyPriceKind,
                EntsoePriceSeriesSource.SourceId,
                horizonStart,
                horizonEnd,
                timeStep),
            CancellationToken.None);

        var asset = new BatteryAsset("single-bess-1", 624, 160, 160, 13, 99, 0.95, 0.95, 25, -10, 55);
        var solverOptions = new ScheduleSolverOptions
        {
            InitialSocPercent = 13,
            DegradationCost = new DegradationCostOptions
            {
                EurPerKwhThroughput = 0.02,
                NominalCRate = 0.5,
                PiecewiseSegments = 8,
            },
        };
        var optimizer = new OrToolsScheduleOptimizer(
            solverOptions,
            new FixedClock(horizonStart.ToUniversalTime()),
            NullLogger<OrToolsScheduleOptimizer>.Instance);
        var command = new ScheduleOptimizationCommand(
            asset.AssetId,
            ScheduleType.DayAhead,
            asset,
            horizonStart,
            horizonEnd,
            timeStep,
            prices.Values,
            prices.Unit);
        var request = new ScheduleOptimizationRequest(command, entsoeOptions.DomainCode, 0);
        var result = await optimizer.OptimizeAsync(request, CancellationToken.None);
        var schedule = result.ProducedSchedule
            ?? throw new InvalidOperationException($"No schedule: {result.Run.Status} {result.Run.TerminationReason}");
        var economics = ScheduleEconomicsCalculator.Calculate(schedule, prices.Values, prices.Unit, asset);

        var output = new
        {
            date = horizonStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            horizon_start = horizonStart,
            horizon_end = horizonEnd,
            price_unit = prices.Unit,
            prices = prices.Values.Select(v => Math.Round(v, 2)).ToArray(),
            optimizer = new
            {
                status = result.Run.Status.ToString(),
                termination_reason = result.Run.TerminationReason,
                objective_value = Math.Round(result.Run.ObjectiveValue, 6),
                objective_breakdown = result.Run.ObjectiveBreakdown.Components.Select(c => new
                {
                    c.Name,
                    value = Math.Round(c.Value, 6),
                    c.Unit,
                }).ToArray(),
            },
            economics = new
            {
                total_cost = Math.Round(economics.TotalCost, 3),
                total_revenue = Math.Round(economics.TotalRevenue, 3),
                total_losses_kwh = Math.Round(economics.TotalLossesKwh, 3),
                net_profit = Math.Round(economics.NetProfit, 3),
                currency = economics.Currency,
            },
            schedule = schedule.Windows.Select((window, index) => new
            {
                hour = window.Start.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                price = Math.Round(prices.Values[index], 2),
                target_power_kw = Math.Round(window.TargetPowerKw, 3),
                soc_end_percent = Math.Round(EstimateSocEndPercent(asset, solverOptions.InitialSocPercent!.Value, schedule.Windows.Take(index + 1)), 3),
                step_net_profit = Math.Round(economics.Steps[index].NetProfit, 3),
                cumulative_net_profit = Math.Round(economics.Steps[index].CumulativeNetProfit, 3),
            }).ToArray(),
        };

        var outPath = Path.Combine(root, "tmp", $"ems-tomorrow-{horizonStart:yyyyMMdd}.json");
        await File.WriteAllTextAsync(
            outPath,
            JsonSerializer.Serialize(output, JsonOptions));

        Assert.Equal(OptimizationSolverStatus.Optimal, result.Run.Status);
        Assert.Equal(24, schedule.Windows.Count);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BatteryEms.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root not found.");
    }

    private static Dictionary<string, string> ReadDotEnv(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('=', StringComparison.Ordinal)) continue;
            var idx = line.IndexOf('=', StringComparison.Ordinal);
            map[line[..idx].Trim()] = line[(idx + 1)..].Trim().Trim('"');
        }
        return map;
    }

    private static string Required(IReadOnlyDictionary<string, string> env, string key) =>
        env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing env key {key}");

    private static double EstimateSocEndPercent(BatteryAsset asset, double initialSocPercent, IEnumerable<ScheduleWindow> windows)
    {
        var socKwh = initialSocPercent / 100.0 * asset.CapacityKwh;
        foreach (var window in windows)
        {
            var energyKwh = Math.Abs(window.TargetPowerKw) * window.Duration.TotalHours;
            if (window.TargetPowerKw < 0)
            {
                socKwh += energyKwh * asset.ChargeEfficiency;
            }
            else if (window.TargetPowerKw > 0)
            {
                socKwh -= energyKwh / asset.DischargeEfficiency;
            }
        }
        return socKwh / asset.CapacityKwh * 100.0;
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
