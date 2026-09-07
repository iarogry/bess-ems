using BatteryEms.Domain;

namespace BatteryEms.Application.Optimization;

public sealed record ScheduleEconomicsReport(
    string PriceUnit,
    string Currency,
    double TotalCost,
    double TotalRevenue,
    double TotalLossesKwh,
    double NetProfit,
    IReadOnlyList<ScheduleEconomicsStep> Steps);

public sealed record ScheduleEconomicsStep(
    DateTimeOffset Start,
    DateTimeOffset End,
    double Price,
    double TargetPowerKw,
    double EnergyMwh,
    double BatteryEnergyDeltaKwh,
    double LossesKwh,
    double Cost,
    double Revenue,
    double NetProfit,
    double CumulativeNetProfit);

public static class ScheduleEconomicsCalculator
{
    public static ScheduleEconomicsReport Calculate(
        Schedule schedule,
        IReadOnlyList<double> pricesPerStep,
        string priceUnit,
        BatteryAsset? asset = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(pricesPerStep);
        ArgumentException.ThrowIfNullOrWhiteSpace(priceUnit);

        if (pricesPerStep.Count != schedule.Windows.Count)
        {
            throw new ArgumentException(
                $"PricesPerStep has {pricesPerStep.Count} entries but schedule has {schedule.Windows.Count} windows.",
                nameof(pricesPerStep));
        }

        var currency = CurrencyFromPriceUnit(priceUnit);
        var steps = new ScheduleEconomicsStep[schedule.Windows.Count];
        var totalCost = 0.0;
        var totalRevenue = 0.0;
        var totalLossesKwh = 0.0;
        var cumulativeNet = 0.0;

        for (var i = 0; i < schedule.Windows.Count; i++)
        {
            var window = schedule.Windows[i];
            var price = pricesPerStep[i];
            var energyMwh = Math.Abs(window.TargetPowerKw) * window.Duration.TotalHours / 1000.0;
            var physical = CalculatePhysicalEnergy(window, asset);
            var cost = window.TargetPowerKw < 0 ? price * energyMwh : 0.0;
            var revenue = window.TargetPowerKw > 0 ? price * energyMwh : 0.0;
            var net = revenue - cost;

            totalCost += cost;
            totalRevenue += revenue;
            totalLossesKwh += physical.LossesKwh;
            cumulativeNet += net;

            steps[i] = new ScheduleEconomicsStep(
                window.Start,
                window.End,
                price,
                window.TargetPowerKw,
                energyMwh,
                physical.BatteryEnergyDeltaKwh,
                physical.LossesKwh,
                cost,
                revenue,
                net,
                cumulativeNet);
        }

        return new ScheduleEconomicsReport(
            priceUnit,
            currency,
            totalCost,
            totalRevenue,
            totalLossesKwh,
            totalRevenue - totalCost,
            steps);
    }

    private static (double BatteryEnergyDeltaKwh, double LossesKwh) CalculatePhysicalEnergy(
        ScheduleWindow window,
        BatteryAsset? asset)
    {
        if (asset is null)
        {
            return (0, 0);
        }

        var gridEnergyKwh = Math.Abs(window.TargetPowerKw) * window.Duration.TotalHours;
        if (window.TargetPowerKw < 0)
        {
            var storedKwh = gridEnergyKwh * asset.ChargeEfficiency;
            return (storedKwh, gridEnergyKwh - storedKwh);
        }
        if (window.TargetPowerKw > 0)
        {
            var batteryDrawKwh = gridEnergyKwh / asset.DischargeEfficiency;
            return (-batteryDrawKwh, batteryDrawKwh - gridEnergyKwh);
        }

        return (0, 0);
    }

    private static string CurrencyFromPriceUnit(string priceUnit)
    {
        var separator = priceUnit.IndexOf('/', StringComparison.Ordinal);
        return separator <= 0 ? priceUnit : priceUnit[..separator];
    }
}
