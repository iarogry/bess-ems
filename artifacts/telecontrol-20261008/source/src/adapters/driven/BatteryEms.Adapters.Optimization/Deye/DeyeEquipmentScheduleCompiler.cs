using System.Text.Json;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Assets;
using BatteryEms.Application.Planning;
using BatteryEms.Domain;

namespace BatteryEms.Adapters.Optimization.Deye;

public sealed class DeyeEquipmentScheduleCompiler(ShadowDeyeProjectionOptions options, IBatteryAssetRegistry assets) : IEquipmentScheduleCompiler
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    public string IntegrationId => "deye_cloud";

    public IReadOnlyList<EquipmentPlanAction> Compile(
        EquipmentPlanningTarget target, DateOnly deliveryDate, Schedule schedule)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(schedule);
        var asset = assets.Find(target.AssetId) ?? throw new InvalidOperationException("deye-physical-asset-missing");
        if (Math.Max(asset.MinSocPercent, target.ReserveSocPercent ?? asset.MinSocPercent) < options.MinimumSocPercent)
        { throw new InvalidOperationException("deye-operational-reserve-not-in-ems-model"); }
        if (asset.MaxSocPercent > options.MaximumSocPercent
            || asset.MaxSocPercent != Math.Floor(asset.MaxSocPercent)
            || Math.Max(asset.MinSocPercent, target.ReserveSocPercent ?? asset.MinSocPercent)
                != Math.Ceiling(Math.Max(asset.MinSocPercent, target.ReserveSocPercent ?? asset.MinSocPercent)))
        { throw new InvalidOperationException("deye-soc-bounds-not-representable"); }
        if (schedule.Windows.Any(window => !double.IsFinite(window.TargetPowerKw)
            || window.TargetPowerKw < -asset.MaxChargePowerKw || window.TargetPowerKw > asset.MaxDischargePowerKw))
        { throw new InvalidOperationException("deye-plan-outside-physical-power-limits"); }
        var bounded = options with
        {
            MinimumSocPercent = Math.Max(options.MinimumSocPercent,
                (int)Math.Ceiling(Math.Max(asset.MinSocPercent, target.ReserveSocPercent ?? asset.MinSocPercent))),
            MaximumSocPercent = Math.Min(options.MaximumSocPercent, (int)Math.Floor(asset.MaxSocPercent)),
        };
        // The legacy comparison shape uses site identity. Equipment identity
        // remains unchanged in the universal EMS schedule.
        var deviceSchedule = new Schedule(target.SiteId, schedule.Type, schedule.MarketBidArea,
            schedule.Version, schedule.Windows);
        var payload = ScheduleShadowPlanProjector.Project(target.SiteId, deliveryDate, deviceSchedule, bounded);
        if (!payload.PayloadReady)
        { throw new InvalidOperationException(string.Join(',', payload.BlockingCodes)); }
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return payload.Windows.Select(window =>
        {
            var timing = ActivationWindowTimingPolicy.Evaluate(deliveryDate, window.WindowId, schedule.HorizonStart);
            return new EquipmentPlanAction(window.WindowId, timing.ScheduledAtUtc!.Value,
                timing.StartDeadlineUtc!.Value, json);
        }).ToArray();
    }
}
