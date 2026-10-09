using BatteryEms.Domain;

namespace BatteryEms.Application.Planning;

public sealed record BatteryInitialStateProjection(double SocPercent, string Basis)
{
    // SOC at 15:00 is a measurement, not a measurement of tomorrow's midnight.
    // Project the remaining active commitments and explicitly label the hold
    // assumption when no current schedule exists.
    public static BatteryInitialStateProjection Calculate(BatteryAsset asset, double measuredSoc,
        DateTimeOffset now, DateTimeOffset horizonStart, Schedule? active)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var energy = measuredSoc / 100 * asset.CapacityKwh;
        if (active is null) { return new(measuredSoc, "hold-latest-soc-assumption"); }
        foreach (var window in active.Windows)
        {
            var from = window.Start > now ? window.Start : now;
            var to = window.End < horizonStart ? window.End : horizonStart;
            if (to <= from) { continue; }
            var power = window.TargetPowerKw;
            if (!double.IsFinite(power) || power < -asset.MaxChargePowerKw || power > asset.MaxDischargePowerKw)
            { throw new InvalidOperationException("planning-current-schedule-outside-physical-limits"); }
            var delta = power < 0 ? -power * asset.ChargeEfficiency : -power / asset.DischargeEfficiency;
            energy = Math.Clamp(energy + delta * (to - from).TotalHours,
                asset.MinSocPercent / 100 * asset.CapacityKwh, asset.MaxSocPercent / 100 * asset.CapacityKwh);
        }
        return new(energy / asset.CapacityKwh * 100, "active-schedule-with-idle-gaps-forecast");
    }
}
