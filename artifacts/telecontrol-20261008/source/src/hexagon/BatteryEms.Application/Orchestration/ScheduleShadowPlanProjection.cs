using System.Globalization;
using BatteryEms.Application.Markets;
using BatteryEms.Domain;

namespace BatteryEms.Application.Orchestration;

public sealed record ShadowDeyeProjectionOptions(
    int ParallelInverterCount = 2,
    int PerInverterPowerLimitWatts = 80_000,
    int MinimumSocPercent = 30,
    int MaximumSocPercent = 100,
    int Voltage = 290)
{
    public ShadowDeyeProjectionOptions EnsureValid()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ParallelInverterCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PerInverterPowerLimitWatts);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumSocPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumSocPercent, 100);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaximumSocPercent, MinimumSocPercent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Voltage);
        return this;
    }
}

/// <summary>
/// Side-effect-free projection from the product schedule model to the bounded
/// four-by-six Deye comparison shape. It never publishes a device command.
/// </summary>
public static class ScheduleShadowPlanProjector
{
    private const double PowerToleranceKw = 0.000_001;
    private static readonly (string Id, string Code, int StartHour, int EndHour)[] Zones =
    [
        ("Z1", "z1", 0, 6),
        ("Z2", "z2", 6, 12),
        ("Z3", "z3", 12, 18),
        ("Z4", "z4", 18, 24),
    ];

    public static ShadowPlanSnapshot Project(
        string siteId,
        DateOnly deliveryDate,
        Schedule? schedule,
        ShadowDeyeProjectionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        ArgumentNullException.ThrowIfNull(options);
        options = options.EnsureValid();
        if (schedule is null)
        {
            return Blocked(siteId, deliveryDate, "shadow-day-ahead-schedule-missing");
        }

        if (!string.Equals(schedule.AssetId, siteId, StringComparison.Ordinal)
            || schedule.Type != ScheduleType.DayAhead)
        {
            return Blocked(siteId, deliveryDate, "shadow-day-ahead-schedule-identity-mismatch");
        }

        var horizon = ShadowDeliveryTime.GetUtcHorizon(deliveryDate);
        if (horizon.End - horizon.Start != TimeSpan.FromHours(24))
        {
            return Blocked(siteId, deliveryDate, "shadow-dst-day-unsupported");
        }

        if (schedule.Windows.Count != 24
            || schedule.HorizonStart != horizon.Start
            || schedule.HorizonEnd != horizon.End)
        {
            return Blocked(siteId, deliveryDate, "shadow-schedule-horizon-invalid");
        }

        for (var hour = 0; hour < schedule.Windows.Count; hour++)
        {
            var window = schedule.Windows[hour];
            if (window.Start != horizon.Start.AddHours(hour)
                || window.End != horizon.Start.AddHours(hour + 1))
            {
                return Blocked(siteId, deliveryDate, "shadow-schedule-grid-invalid");
            }
        }

        var windows = new List<ShadowTouWindow>(Zones.Length);
        foreach (var zone in Zones)
        {
            var projection = ProjectZone(zone, schedule.Windows, options);
            if (projection is null)
            {
                return Blocked(siteId, deliveryDate, $"shadow-{zone.Code}-not-representable");
            }

            windows.Add(projection);
        }

        return new ShadowPlanSnapshot(siteId, deliveryDate, true, [], windows).EnsureValid();
    }

    private static ShadowTouWindow? ProjectZone(
        (string Id, string Code, int StartHour, int EndHour) zone,
        IReadOnlyList<ScheduleWindow> schedule,
        ShadowDeyeProjectionOptions options)
    {
        var segments = new List<Segment>();
        for (var hour = 0; hour < 24; hour++)
        {
            var isActive = hour >= zone.StartHour && hour < zone.EndHour;
            var settings = isActive
                ? SettingsFromTarget(schedule[hour].TargetPowerKw, options)
                : SafeSettings(options);
            if (settings is null)
            {
                return null;
            }

            if (segments.Count > 0 && segments[^1].Settings == settings)
            {
                segments[^1] = segments[^1] with
                {
                    EndHour = hour + 1,
                    ContainsActiveHour = segments[^1].ContainsActiveHour || isActive,
                };
            }
            else
            {
                segments.Add(new Segment(hour, hour + 1, isActive, settings));
            }
        }

        if (segments.Count > 6)
        {
            return null;
        }

        while (segments.Count < 6)
        {
            var splitIndex = FindSplitCandidate(segments);
            if (splitIndex < 0)
            {
                return null;
            }

            var segment = segments[splitIndex];
            var splitHour = segment.EndHour - 1;
            segments[splitIndex] = segment with { EndHour = splitHour };
            segments.Insert(
                splitIndex + 1,
                segment with { StartHour = splitHour });
        }

        return new ShadowTouWindow(
            zone.Id,
            segments.Select(segment => new ShadowTouInterval(
                $"{segment.StartHour:00}:00",
                segment.Settings.EnableGeneration,
                segment.Settings.EnableGridCharge,
                segment.Settings.EnableSell,
                segment.Settings.PowerWatts,
                segment.Settings.SocPercent,
                segment.Settings.Voltage)).ToArray());
    }

    private static int FindSplitCandidate(IReadOnlyList<Segment> segments)
    {
        var candidate = segments
            .Select((segment, index) => new { Segment = segment, Index = index })
            .Where(item => item.Segment.EndHour - item.Segment.StartHour > 1)
            .OrderByDescending(item => item.Segment.ContainsActiveHour)
            .ThenByDescending(item => item.Segment.EndHour - item.Segment.StartHour)
            .ThenBy(item => item.Segment.StartHour)
            .FirstOrDefault();
        return candidate?.Index ?? -1;
    }

    private static Settings? SettingsFromTarget(
        double targetPowerKw,
        ShadowDeyeProjectionOptions options)
    {
        if (!double.IsFinite(targetPowerKw))
        {
            return null;
        }

        var requestedWatts = Math.Round(
            Math.Abs(targetPowerKw) * 1000 / options.ParallelInverterCount,
            MidpointRounding.AwayFromZero);
        if (requestedWatts > options.PerInverterPowerLimitWatts
            || requestedWatts > int.MaxValue)
        {
            return null;
        }
        var perInverterWatts = (int)requestedWatts;

        if (targetPowerKw < -PowerToleranceKw)
        {
            return new Settings(true, true, false, perInverterWatts, options.MaximumSocPercent, options.Voltage);
        }

        if (targetPowerKw > PowerToleranceKw)
        {
            return new Settings(true, false, true, perInverterWatts, options.MinimumSocPercent, options.Voltage);
        }

        return SafeSettings(options);
    }

    private static Settings SafeSettings(ShadowDeyeProjectionOptions options) =>
        new(true, false, false, 0, options.MinimumSocPercent, options.Voltage);

    private static ShadowPlanSnapshot Blocked(
        string siteId,
        DateOnly deliveryDate,
        string code) =>
        new(siteId, deliveryDate, false, [code], []);

    private sealed record Settings(
        bool EnableGeneration,
        bool EnableGridCharge,
        bool EnableSell,
        int PowerWatts,
        int SocPercent,
        int Voltage);

    private sealed record Segment(
        int StartHour,
        int EndHour,
        bool ContainsActiveHour,
        Settings Settings);
}

public sealed class ScheduleShadowPlanProjectionModule : IOrchestrationModule
{
    private readonly IScheduleRepository _schedules;
    private readonly IShadowPlanComparisonStore _store;
    private readonly ShadowDeyeProjectionOptions _options;

    public ScheduleShadowPlanProjectionModule(
        IScheduleRepository schedules,
        IShadowPlanComparisonStore store,
        ShadowDeyeProjectionOptions options)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string ModuleName => "schedule_shadow_plan_projection";

    public int StepOrder => 95;

    public async Task<OrchestrationModuleResult> ExecuteAsync(
        OrchestrationModuleContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(
                context.RunType,
                ShadowPlanComparisonModule.SupportedRunType,
                StringComparison.Ordinal))
        {
            return new OrchestrationModuleResult(OrchestrationStepStatus.Skipped);
        }

        if (!DateOnly.TryParseExact(
                context.TriggerRef,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var deliveryDate))
        {
            return Blocked("shadow-delivery-date-invalid");
        }

        var snapshot = ScheduleShadowPlanProjector.Project(
            context.SiteId,
            deliveryDate,
            _schedules.FindActive(context.SiteId, ScheduleType.DayAhead),
            _options);
        await _store.PutSnapshotAsync(
            ShadowPlanSide.Shadow,
            snapshot,
            cancellationToken).ConfigureAwait(false);

        return snapshot.PayloadReady
            ? new OrchestrationModuleResult(
                OrchestrationStepStatus.Succeeded,
                DataRole.Informational,
                DataBalanceState.Ready,
                OutputRef: $"schedule-shadow-snapshot:{context.SiteId}:{deliveryDate:yyyy-MM-dd}")
            : Blocked(snapshot.BlockingCodes[0]);
    }

    private static OrchestrationModuleResult Blocked(string code) =>
        new(
            OrchestrationStepStatus.Blocked,
            DataRole.Informational,
            DataBalanceState.Blocked,
            ErrorCode: code,
            ErrorMessage: "The active day-ahead schedule cannot be projected to a bounded shadow plan.");
}

internal static class ShadowDeliveryTime
{
    internal static DateTimeOffset ToUtc(DateOnly localDate, TimeOnly localTime)
    {
        var zone = ResolveKyivTimeZone();
        var local = localDate.ToDateTime(localTime, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
        {
            throw new ArgumentException("Activation trigger time is invalid or ambiguous.", nameof(localTime));
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

    internal static (DateTimeOffset Start, DateTimeOffset End) GetUtcHorizon(DateOnly deliveryDate)
    {
        var timeZone = ResolveKyivTimeZone();
        var localStart = deliveryDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var localEnd = deliveryDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return (
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone)),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone)));
    }

    private static TimeZoneInfo ResolveKyivTimeZone()
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Kyiv", out var timeZone)
            || TimeZoneInfo.TryFindSystemTimeZoneById("FLE Standard Time", out timeZone))
        {
            return timeZone;
        }

        throw new InvalidOperationException("Europe/Kyiv time zone is unavailable.");
    }
}
