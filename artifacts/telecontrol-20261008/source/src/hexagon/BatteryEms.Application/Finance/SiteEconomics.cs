namespace BatteryEms.Application.Finance;

public sealed record SiteEnergyInterval(
    DateTimeOffset Start,
    double KguGenerationKwh,
    double PvGenerationKwh,
    double BatteryChargeKwh,
    double BatteryDischargeKwh,
    double GridImportKwh,
    double GridExportKwh,
    double SubscribersImportKwh,
    double SubscribersExportKwh);

public sealed record SiteEconomicParameters(
    double DistributionPerKwh,
    double TransmissionPerKwh,
    double ReleaseCoefficient,
    double KguCostPerKwh,
    double PvCostPerKwh,
    double BatteryDegradationPerKwh,
    double FixedDailyCost,
    double BalanceToleranceKwh)
{
    public SiteEconomicParameters EnsureValid()
    {
        double[] values = [DistributionPerKwh, TransmissionPerKwh, KguCostPerKwh,
            PvCostPerKwh, BatteryDegradationPerKwh, FixedDailyCost, BalanceToleranceKwh];
        if (values.Any(value => !double.IsFinite(value) || value < 0)
            || !double.IsFinite(ReleaseCoefficient) || ReleaseCoefficient is < 0 or > 1)
        {
            throw new ArgumentException("Invalid financial parameters.");
        }
        return this;
    }
}

public sealed record SiteEconomicResult(
    double OwnUseSaving,
    double ExportRevenue,
    double BatteryChargeCost,
    double OperatingCost,
    double GrossEffect,
    double PlannedProfit,
    double UnallocatedEnergyKwh);

public static class SiteEconomics
{
    public static SiteEconomicResult Calculate(
        IReadOnlyList<SiteEnergyInterval> intervals,
        IReadOnlyList<double> pricesPerKwh,
        SiteEconomicParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentNullException.ThrowIfNull(pricesPerKwh);
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.EnsureValid();
        if (intervals.Count == 0 || intervals.Count != pricesPerKwh.Count)
        {
            throw new ArgumentException("Energy and prices must cover the same intervals.");
        }
        var saving = 0.0;
        var revenue = 0.0;
        var chargeCost = 0.0;
        var operatingCost = parameters.FixedDailyCost;
        var unallocated = 0.0;
        for (var index = 0; index < intervals.Count; index++)
        {
            var energy = intervals[index];
            ValidateEnergy(energy);
            var price = pricesPerKwh[index];
            if (!double.IsFinite(price)) throw new ArgumentException("Invalid market price.");
            var source = energy.KguGenerationKwh + energy.PvGenerationKwh + energy.BatteryDischargeKwh;
            var subscriberNet = energy.SubscribersImportKwh - energy.SubscribersExportKwh;
            var ownNeeds = source + energy.GridImportKwh - subscriberNet
                - energy.BatteryChargeKwh - energy.GridExportKwh;
            if (ownNeeds < -parameters.BalanceToleranceKwh)
            {
                throw new ArgumentException("Negative site own-needs balance.");
            }
            var own = Math.Clamp(ownNeeds, 0, source);
            var remaining = source - own;
            var exported = Math.Clamp(energy.GridExportKwh + subscriberNet, 0, remaining);
            remaining -= exported;
            var sourceToCharge = Math.Min(energy.BatteryChargeKwh, remaining);
            unallocated += remaining - sourceToCharge;
            var fullPrice = price + parameters.DistributionPerKwh + parameters.TransmissionPerKwh;
            saving += own * fullPrice;
            revenue += exported * price * parameters.ReleaseCoefficient;
            // Use measured/projected charge, never overwrite it with a source allocation.
            chargeCost += energy.BatteryChargeKwh * fullPrice;
            operatingCost += energy.KguGenerationKwh * parameters.KguCostPerKwh
                + energy.PvGenerationKwh * parameters.PvCostPerKwh
                + energy.BatteryDischargeKwh * parameters.BatteryDegradationPerKwh;
        }
        var gross = saving + revenue;
        var profit = gross - chargeCost - operatingCost;
        double[] totals = [saving, revenue, chargeCost, operatingCost, gross, profit, unallocated];
        if (totals.Any(value => !double.IsFinite(value))) throw new ArgumentException("Financial result overflow.");
        return new SiteEconomicResult(saving, revenue, chargeCost, operatingCost, gross, profit, unallocated);
    }

    private static void ValidateEnergy(SiteEnergyInterval energy)
    {
        ArgumentNullException.ThrowIfNull(energy);
        double[] values = [energy.KguGenerationKwh, energy.PvGenerationKwh, energy.BatteryChargeKwh,
            energy.BatteryDischargeKwh, energy.GridImportKwh, energy.GridExportKwh,
            energy.SubscribersImportKwh, energy.SubscribersExportKwh];
        if (values.Any(value => !double.IsFinite(value) || value < 0))
        {
            throw new ArgumentException("Energy must be finite, nonnegative interval kWh.");
        }
    }
}
