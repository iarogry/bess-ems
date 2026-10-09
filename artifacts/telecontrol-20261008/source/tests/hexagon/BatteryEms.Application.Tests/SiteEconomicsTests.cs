using BatteryEms.Application.Finance;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class SiteEconomicsTests
{
    private static readonly SiteEconomicParameters Parameters = new(2, 1, 0.965, 0, 0, 0, 0, 0.01);

    [Fact]
    public void Matches_source_allocation_and_keeps_battery_charge_as_measured_energy()
    {
        SiteEnergyInterval[] intervals =
        [new(DateTimeOffset.UtcNow, 100, 0, 30, 0, 0, 20, 10, 0)];
        var result = SiteEconomics.Calculate(intervals, [5], Parameters);
        // Own needs 40, commercial export 30, source-to-charge 30.
        Assert.Equal(320, result.OwnUseSaving);
        Assert.Equal(144.75, result.ExportRevenue, precision: 6);
        Assert.Equal(240, result.BatteryChargeCost);
        Assert.Equal(224.75, result.PlannedProfit, precision: 6);
        Assert.Equal(0, result.UnallocatedEnergyKwh);
        Assert.Equal(30, intervals[0].BatteryChargeKwh);
    }

    [Fact]
    public void Uses_configured_operating_costs_once_without_subtracting_grid_import_again()
    {
        var parameters = Parameters with { KguCostPerKwh = 2, PvCostPerKwh = 1,
            BatteryDegradationPerKwh = 0.5, FixedDailyCost = 10 };
        var result = SiteEconomics.Calculate(
            [new(DateTimeOffset.UtcNow, 10, 10, 0, 10, 50, 0, 0, 0)], [5], parameters);
        Assert.Equal(240, result.GrossEffect);
        Assert.Equal(45, result.OperatingCost);
        Assert.Equal(195, result.PlannedProfit);
    }

    [Fact]
    public void Negative_market_prices_remain_negative()
    {
        var result = SiteEconomics.Calculate(
            [new(DateTimeOffset.UtcNow, 0, 10, 0, 0, 0, 10, 0, 0)], [-5], Parameters);
        Assert.Equal(-48.25, result.ExportRevenue, precision: 6);
    }

    [Fact]
    public void Rejects_negative_own_needs_instead_of_hiding_the_balance_error()
    {
        Assert.Throws<ArgumentException>(() => SiteEconomics.Calculate(
            [new(DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 10, 0, 0)], [5], Parameters));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void Rejects_invalid_energy(double energy)
    {
        Assert.Throws<ArgumentException>(() => SiteEconomics.Calculate(
            [new(DateTimeOffset.UtcNow, energy, 0, 0, 0, 0, 0, 0, 0)], [5], Parameters));
    }

    [Fact]
    public void Monthly_review_expires_at_month_boundary_and_on_any_parameter_change()
    {
        var date = new DateOnly(2026, 10, 7);
        var review = Review();
        Assert.True(review.Covers(date, "v1", "operator", "2", Parameters, "UAH", "EUR", 40));
        Assert.False(review.Covers(date.AddMonths(1), "v1", "operator", "2", Parameters, "UAH", "EUR", 40));
        Assert.False(review.Covers(date, "v1", "operator", "2", Parameters with { ReleaseCoefficient = 0.9 }, "UAH", "EUR", 40));
        Assert.False(review.Covers(date, "v1", "operator", "2", Parameters, "UAH", "EUR", 41));
        Assert.False((review with { HasConflict = true }).Covers(date, "v1", "operator", "2", Parameters, "UAH", "EUR", 40));
        Assert.False((review with { Confirmed = false }).Covers(date, "v1", "operator", "2", Parameters, "UAH", "EUR", 40));
    }

    private static MonthlyFinancialReview Review() => new(
        new(2026, 10, 1), new(2026, 10, 1), new(2026, 10, 1), new(2026, 10, 31),
        "v1", "operator", "2", new Uri("https://www.nerc.gov.ua/distribution"), "resolution-D",
        new Uri("https://www.nerc.gov.ua/transmission"), "resolution-T", "contract", "FX evidence",
        Parameters, "UAH", "EUR", 40, true, false);
}
