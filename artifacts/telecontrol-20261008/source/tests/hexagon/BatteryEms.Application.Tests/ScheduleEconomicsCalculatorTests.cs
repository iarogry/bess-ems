using BatteryEms.Application.Optimization;
using BatteryEms.Domain;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class ScheduleEconomicsCalculatorTests
{
    private static readonly DateTimeOffset HorizonStart =
        new(2026, 6, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly double[] Prices = { 10.0, 10000.0, 5000.0 };

    [Fact]
    public void Calculate_reports_cost_revenue_net_and_cumulative_profit_per_step()
    {
        var schedule = new Schedule(
            "asset-1",
            ScheduleType.DayAhead,
            "10Y1001C--000182",
            version: 1,
            new[]
            {
                new ScheduleWindow(HorizonStart, HorizonStart + TimeSpan.FromHours(1), -160),
                new ScheduleWindow(HorizonStart + TimeSpan.FromHours(1), HorizonStart + TimeSpan.FromHours(2), 160),
                new ScheduleWindow(HorizonStart + TimeSpan.FromHours(2), HorizonStart + TimeSpan.FromHours(3), 0),
            });

        var report = ScheduleEconomicsCalculator.Calculate(
            schedule,
            Prices,
            "UAH/MWh",
            SampleAsset());

        Assert.Equal("UAH/MWh", report.PriceUnit);
        Assert.Equal("UAH", report.Currency);
        Assert.Equal(1.6, report.TotalCost, precision: 6);
        Assert.Equal(1600.0, report.TotalRevenue, precision: 6);
        Assert.Equal(16.4210526316, report.TotalLossesKwh, precision: 6);
        Assert.Equal(1598.4, report.NetProfit, precision: 6);

        Assert.Equal(152.0, report.Steps[0].BatteryEnergyDeltaKwh, precision: 6);
        Assert.Equal(8.0, report.Steps[0].LossesKwh, precision: 6);
        Assert.Equal(-168.4210526316, report.Steps[1].BatteryEnergyDeltaKwh, precision: 6);
        Assert.Equal(8.4210526316, report.Steps[1].LossesKwh, precision: 6);
        Assert.Equal(-1.6, report.Steps[0].NetProfit, precision: 6);
        Assert.Equal(-1.6, report.Steps[0].CumulativeNetProfit, precision: 6);
        Assert.Equal(1600.0, report.Steps[1].Revenue, precision: 6);
        Assert.Equal(1598.4, report.Steps[1].CumulativeNetProfit, precision: 6);
        Assert.Equal(0.0, report.Steps[2].NetProfit, precision: 6);
    }

    [Fact]
    public void Calculate_rejects_price_count_that_does_not_match_schedule()
    {
        var schedule = new Schedule(
            "asset-1",
            ScheduleType.DayAhead,
            "DE-LU",
            version: 1,
            new[] { new ScheduleWindow(HorizonStart, HorizonStart + TimeSpan.FromHours(1), 0) });

        Assert.Throws<ArgumentException>(() =>
            ScheduleEconomicsCalculator.Calculate(schedule, Array.Empty<double>(), "EUR/MWh"));
    }

    private static BatteryAsset SampleAsset() => new(
        assetId: "asset-1",
        capacityKwh: 624,
        maxChargePowerKw: 160,
        maxDischargePowerKw: 160,
        minSocPercent: 13,
        maxSocPercent: 99,
        chargeEfficiency: 0.95,
        dischargeEfficiency: 0.95,
        maxRampKwPerSecond: 25,
        minOperatingTemperatureCelsius: -10,
        maxOperatingTemperatureCelsius: 55);
}
