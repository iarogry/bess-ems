using System.Text.Json;
using BatteryEms.Adapters.Optimization.Deye;
using Xunit;

namespace BatteryEms.Adapters.Optimization.Tests;

public sealed class LegacyDeyeScenarioFileSourceTests : IDisposable
{
    private static readonly DateOnly DeliveryDate = new(2026, 9, 23);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"bess-shadow-{Guid.NewGuid():N}");

    public LegacyDeyeScenarioFileSourceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Executable_scenario_maps_only_bounded_tou_fields()
    {
        await WriteAsync("deye-tou-2026-09-23.json", ExecutableArtifact());
        var source = new LegacyDeyeScenarioFileSource(_directory);

        var result = await source.LoadAsync(
            "site-reference",
            DeliveryDate,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Snapshot);
        Assert.True(result.Snapshot!.PayloadReady);
        Assert.Equal(4, result.Snapshot.Windows.Count);
        Assert.All(result.Snapshot.Windows, window => Assert.Equal(6, window.Intervals.Count));
        Assert.Equal(80_000, result.Snapshot.Windows[0].Intervals[1].PowerWatts);
    }

    [Fact]
    public async Task Blocked_artifact_maps_reason_codes_without_executable_windows()
    {
        await WriteAsync("deye-tou-2026-09-23.blocked.json", new
        {
            target_date = "2026-09-23",
            payloadReady = false,
            blockedReasons = new[]
            {
                new { code = "OREE_DAM_TARGET_ROW_MISSING", message = "not imported" },
            },
            secret = "must-not-enter-snapshot",
        });
        var source = new LegacyDeyeScenarioFileSource(_directory);

        var result = await source.LoadAsync(
            "site-reference",
            DeliveryDate,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Snapshot!.PayloadReady);
        Assert.Equal(["OREE_DAM_TARGET_ROW_MISSING"], result.Snapshot.BlockingCodes);
        Assert.Empty(result.Snapshot.Windows);
        Assert.DoesNotContain(
            "must-not-enter-snapshot",
            JsonSerializer.Serialize(result.Snapshot),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_artifact_is_a_typed_fail_closed_result()
    {
        var source = new LegacyDeyeScenarioFileSource(_directory);

        var result = await source.LoadAsync(
            "site-reference",
            DeliveryDate,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("legacy-scenario-missing", result.ErrorCode);
    }

    [Fact]
    public async Task Artifact_for_a_different_delivery_date_is_rejected()
    {
        await WriteAsync("deye-tou-2026-09-23.json", new
        {
            target_date = "2026-09-24",
            payloadReady = false,
            blockedReasons = new[] { new { code = "TEST" } },
        });
        var source = new LegacyDeyeScenarioFileSource(_directory);

        var result = await source.LoadAsync(
            "site-reference",
            DeliveryDate,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("legacy-scenario-invalid", result.ErrorCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task WriteAsync(string name, object value)
    {
        var path = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value));
    }

    private static object ExecutableArtifact() => new
    {
        target_date = "2026-09-23",
        payloadReady = true,
        stationId = "ignored-vendor-id",
        touWindows = Enumerable.Range(1, 4).Select(index => new
        {
            id = $"Z{index}",
            intervals = new[]
            {
                Interval("00:00", 0),
                Interval("01:00", 80_000),
                Interval("02:00", 0),
                Interval("03:00", 80_000),
                Interval("04:00", 0),
                Interval("05:00", 0),
            },
        }),
    };

    private static object Interval(string time, int power) => new
    {
        time,
        enableGeneration = true,
        enableGridCharge = false,
        enableSell = power > 0,
        power,
        soc = 30,
        voltage = 290,
        deviceSn = "ignored-vendor-id",
    };
}
