namespace BatteryEms.Adapters.Optimization.OrTools;

// RM-M2-04: throughput proxy for LH-OPT-004 "Batteriealterungskosten".
// Currency per kWh of energy passing through the battery (charge +
// discharge each contribute their absolute value because both stress
// the cells). By default this is linear. When NominalCRate is set, the
// rate is interpreted at that nominal C-rate and a convex piecewise-LP
// approximation makes per-kWh degradation grow with power/current.
//
// Setting `EurPerKwhThroughput = 0` keeps the component active in the
// objective breakdown (so dashboards see a zero entry instead of a
// missing one) without applying a penalty.
public sealed record DegradationCostOptions
{
    public required double EurPerKwhThroughput { get; init; }
    public double? NominalCRate { get; init; }
    public int PiecewiseSegments { get; init; } = 8;

    public DegradationCostOptions EnsureValid()
    {
        if (!double.IsFinite(EurPerKwhThroughput) || EurPerKwhThroughput < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EurPerKwhThroughput),
                EurPerKwhThroughput,
                "EurPerKwhThroughput must be finite and non-negative.");
        }
        if (NominalCRate is { } nominal && (!double.IsFinite(nominal) || nominal <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(NominalCRate),
                NominalCRate,
                "NominalCRate must be finite and positive when set.");
        }
        if (PiecewiseSegments <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PiecewiseSegments),
                PiecewiseSegments,
                "PiecewiseSegments must be positive.");
        }
        return this;
    }
}

// RM-M2-04: penalty for SOC deviating from a fixed target percent
// (LH-OPT-004 "Strafkosten für SOC-Zielabweichung"). Modelled in LP via
// two non-negative slack variables per step (`soc_below`, `soc_above`)
// constrained to `target - soc[t]` and `soc[t] - target` respectively;
// the objective penalises their sum so any deviation costs in proportion
// to its magnitude (linear penalty) and duration (sum-over-steps).
//
// The initial SOC step (soc[0]) is excluded because it's pinned by the
// solver options; including it would add a fixed offset to the objective
// that the optimiser cannot influence and would inflate the breakdown
// without changing any decision variable.
public sealed record SocTargetPenaltyOptions
{
    public required double TargetSocPercent { get; init; }
    public required double EurPerPercentDeviation { get; init; }

    public SocTargetPenaltyOptions EnsureValid()
    {
        if (!double.IsFinite(TargetSocPercent) || TargetSocPercent < 0 || TargetSocPercent > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TargetSocPercent),
                TargetSocPercent,
                "TargetSocPercent must be in [0, 100].");
        }
        if (!double.IsFinite(EurPerPercentDeviation) || EurPerPercentDeviation < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EurPerPercentDeviation),
                EurPerPercentDeviation,
                "EurPerPercentDeviation must be finite and non-negative.");
        }
        return this;
    }
}
