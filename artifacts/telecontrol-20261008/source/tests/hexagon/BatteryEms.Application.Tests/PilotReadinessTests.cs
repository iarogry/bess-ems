using BatteryEms.Application.Orchestration;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class PilotReadinessTests
{
    private static readonly DateOnly EndDate = new(2026, 9, 24);
    private static readonly PilotReadinessOptions Options = new();

    [Fact]
    public void Fourteen_consecutive_equivalent_ready_days_qualify()
    {
        var evidence = Evidence(Options.RequiredConsecutiveDays).ToArray();

        var result = PilotReadinessPolicy.Evaluate("site-1", EndDate, Options, evidence);

        Assert.True(result.IsReady);
        Assert.Equal(14, result.PassingDays);
        Assert.Empty(result.BlockingCodes);
    }

    [Fact]
    public void Missing_day_and_mismatch_fail_closed_with_sanitized_codes()
    {
        var evidence = Evidence(Options.RequiredConsecutiveDays)
            .Where(item => item.DeliveryDate != EndDate.AddDays(-5))
            .Select(item => item.DeliveryDate == EndDate.AddDays(-2)
                ? item with
                {
                    IsEquivalent = false,
                    Mismatches = [new ShadowPlanMismatch("power", "1", "2")],
                }
                : item)
            .ToArray();

        var result = PilotReadinessPolicy.Evaluate("site-1", EndDate, Options, evidence);

        Assert.False(result.IsReady);
        Assert.Equal(12, result.PassingDays);
        Assert.Contains("pilot-evidence-missing-day", result.BlockingCodes);
        Assert.Contains("pilot-plan-mismatch", result.BlockingCodes);
    }

    [Fact]
    public async Task Query_uses_latest_comparison_for_each_delivery_date()
    {
        var store = new InMemoryShadowPlanComparisonStore();
        var date = EndDate.AddDays(-1);
        await store.SaveComparisonAsync(Comparison(date, equivalent: true), CancellationToken.None);
        await store.SaveComparisonAsync(
            Comparison(date, equivalent: false) with
            {
                ComparedAtUtc = new DateTimeOffset(2026, 9, 24, 2, 0, 0, TimeSpan.Zero),
                Mismatches = [new ShadowPlanMismatch("soc", "30", "31")],
            },
            CancellationToken.None);

        var result = await store.QueryComparisonsAsync(
            "site-1",
            date,
            date,
            CancellationToken.None);

        var latest = Assert.Single(result);
        Assert.False(latest.IsEquivalent);
        Assert.Single(latest.Mismatches);
    }

    private static IEnumerable<ShadowPlanComparisonRecord> Evidence(int days)
    {
        for (var offset = days - 1; offset >= 0; offset--)
        {
            yield return Comparison(EndDate.AddDays(-offset), equivalent: true);
        }
    }

    private static ShadowPlanComparisonRecord Comparison(DateOnly date, bool equivalent) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "site-1",
        date,
        new DateTimeOffset(date.ToDateTime(new TimeOnly(1, 0)), TimeSpan.Zero),
        equivalent,
        LegacyPayloadReady: true,
        ShadowPayloadReady: true,
        Mismatches: []);
}
