using BatteryEms.Application.Orchestration;
using System.Text.Json;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class ShadowPlanComparisonTests
{
    private static readonly JsonSerializerOptions CachedJson = new(JsonSerializerDefaults.Web);
    [Fact]
    public void Identical_executable_plans_are_equivalent()
    {
        var legacy = LoadGolden("executable-plan.golden.json");

        var result = ShadowPlanComparer.Compare(legacy, legacy with { });

        Assert.True(result.IsEquivalent);
        Assert.Empty(result.Mismatches);
    }

    [Fact]
    public void Power_difference_is_reported_with_exact_path()
    {
        var legacy = LoadGolden("executable-plan.golden.json");
        var changedIntervals = legacy.Windows[0].Intervals.ToArray();
        changedIntervals[1] = changedIntervals[1] with { PowerWatts = 79_990 };
        var changedWindows = legacy.Windows.ToArray();
        changedWindows[0] = changedWindows[0] with { Intervals = changedIntervals };
        var shadow = legacy with
        {
            Windows = changedWindows,
        };

        var result = ShadowPlanComparer.Compare(legacy, shadow);

        Assert.False(result.IsEquivalent);
        var mismatch = Assert.Single(result.Mismatches);
        Assert.Equal("windows[0].intervals[1].power_watts", mismatch.Path);
        Assert.Equal("80000", mismatch.LegacyValue);
        Assert.Equal("79990", mismatch.ShadowValue);
    }

    [Fact]
    public void Same_blocking_codes_are_equivalent_regardless_of_order()
    {
        var legacy = BlockedPlan("OREE_ROW_MISSING", "TELEMETRY_STALE");
        var shadow = BlockedPlan("TELEMETRY_STALE", "OREE_ROW_MISSING");

        var result = ShadowPlanComparer.Compare(legacy, shadow);

        Assert.True(result.IsEquivalent);
    }

    [Fact]
    public void Blocked_golden_contains_no_executable_payload()
    {
        var blocked = LoadGolden("blocked-plan.golden.json");

        Assert.False(blocked.PayloadReady);
        Assert.Equal(["OREE_DAM_TARGET_ROW_MISSING"], blocked.BlockingCodes);
        Assert.Empty(blocked.Windows);
        blocked.EnsureValid();
    }

    [Fact]
    public void Blocked_plan_cannot_carry_executable_windows()
    {
        var invalid = LoadGolden("executable-plan.golden.json") with
        {
            PayloadReady = false,
            BlockingCodes = ["OREE_ROW_MISSING"],
        };

        var error = Assert.Throws<ArgumentException>(() => invalid.EnsureValid());

        Assert.Contains("blocked shadow plan", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Executable_window_requires_six_ordered_start_time_pairs()
    {
        var plan = LoadGolden("executable-plan.golden.json");
        var invalid = plan with
        {
            Windows =
            [
                plan.Windows[0] with
                {
                    Intervals = plan.Windows[0].Intervals.Reverse().ToArray(),
                },
            ],
        };

        Assert.Throws<ArgumentException>(() => invalid.EnsureValid());
    }

    private static ShadowPlanSnapshot BlockedPlan(params string[] codes) =>
        new("site-reference", new DateOnly(2026, 9, 24), false, codes, []);

    private static ShadowPlanSnapshot LoadGolden(string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Productization",
            fileName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ShadowPlanSnapshot>(
                   json,
                   CachedJson)
               ?? throw new InvalidOperationException($"Golden fixture {fileName} is empty.");
    }

}
