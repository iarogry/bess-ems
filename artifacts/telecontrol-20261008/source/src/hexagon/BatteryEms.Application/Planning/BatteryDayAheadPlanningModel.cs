using BatteryEms.Application.Assets;
using BatteryEms.Application.Markets;
using BatteryEms.Application.Optimization;
using BatteryEms.Application.Realtime;
using BatteryEms.Application.Persistence;
using BatteryEms.Domain;

namespace BatteryEms.Application.Planning;

public sealed class BatteryDayAheadPlanningModel(
    IBatteryAssetRegistry assets, ISnapshotStore telemetry, IPriceSeriesSource prices,
    IScheduleOptimizer optimizer, IReserveRepository reserves, IScheduleRepository schedules,
    IOptimizationRunRepository runs) : IEquipmentDayAheadPlanningModel
{
    public string EquipmentKind => "battery";

    public async Task<EquipmentPlanOptimization> OptimizeAsync(
        EquipmentPlanningTarget target, DateOnly deliveryDate, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var asset = assets.Find(target.AssetId) ?? throw new InvalidOperationException("planning-asset-missing");
        if (target.ReserveSocPercent is { } reserve)
        {
            asset = new BatteryAsset(asset.AssetId, asset.CapacityKwh, asset.MaxChargePowerKw,
                asset.MaxDischargePowerKw, Math.Max(asset.MinSocPercent, reserve), asset.MaxSocPercent,
                asset.ChargeEfficiency, asset.DischargeEfficiency, asset.MaxRampKwPerSecond,
                asset.MinOperatingTemperatureCelsius, asset.MaxOperatingTemperatureCelsius);
        }
        var snapshot = telemetry.GetLatest(target.AssetId, now);
        ValidateTelemetry(snapshot, asset, now);
        var (start, end) = EmsPlanningTime.Horizon(deliveryDate);
        var series = await prices.LoadAsync(new PriceSeriesRequest(target.MarketBidArea,
            "day_ahead", "energy_price", "entso-e", start, end, TimeSpan.FromHours(1)), cancellationToken).ConfigureAwait(false);
        if (series.Product != "day_ahead" || series.PriceKind != "energy_price" || series.Source != "entso-e" || series.MarketBidArea != target.MarketBidArea
            || series.Unit != target.PriceUnit || series.HorizonStart != start || series.HorizonEnd != end
            || series.TimeStep != TimeSpan.FromHours(1))
        { throw new InvalidOperationException("planning-price-identity-mismatch"); }
        var initial = BatteryInitialStateProjection.Calculate(asset, snapshot!.Telemetry.SocPercent,
            now, start, schedules.FindActive(target.AssetId, ScheduleType.DayAhead));
        var command = new ScheduleOptimizationCommand(target.AssetId, ScheduleType.DayAhead, asset,
            start, end, series.TimeStep, series.Values, series.Unit,
            initialSocPercent: initial.SocPercent,
            throughputCostPerKwh: target.ThroughputCostPerKwh);
        var result = await optimizer.OptimizeAsync(new ScheduleOptimizationRequest(command,
            target.MarketBidArea, 0, reserves.FindActive(target.AssetId, start, end)), cancellationToken).ConfigureAwait(false);
        await runs.AppendAsync(result.Run, cancellationToken).ConfigureAwait(false);
        var schedule = result.ProducedSchedule ?? throw new InvalidOperationException("planning-no-usable-solution");
        return new EquipmentPlanOptimization(schedule, snapshot.Telemetry.Timestamp,
            initial.SocPercent, result.Run.RunId, initial.Basis);
    }

    private static void ValidateTelemetry(Snapshot? snapshot, BatteryAsset asset, DateTimeOffset now)
    {
        if (snapshot is null || !snapshot.Quality.IsUsableForControl
            || !snapshot.Telemetry.DataQuality.IsUsableForControl || !snapshot.Telemetry.Available
            || snapshot.Telemetry.Timestamp > now || snapshot.ReceivedAt > now
            || now - snapshot.Telemetry.Timestamp > TimeSpan.FromMinutes(5)
            || now - snapshot.ReceivedAt > TimeSpan.FromMinutes(5)
            || !double.IsFinite(snapshot.Telemetry.SocPercent)
            || snapshot.Telemetry.SocPercent < asset.MinSocPercent
            || snapshot.Telemetry.SocPercent > asset.MaxSocPercent
            || !double.IsFinite(snapshot.Telemetry.TemperatureCelsius)
            || snapshot.Telemetry.TemperatureCelsius < asset.MinOperatingTemperatureCelsius
            || snapshot.Telemetry.TemperatureCelsius > asset.MaxOperatingTemperatureCelsius)
        { throw new InvalidOperationException("planning-telemetry-not-usable"); }
    }
}

