namespace BatteryEms.Application.Planning;

public sealed class EmsDayAheadPlanner(
    IEnumerable<IEquipmentDayAheadPlanningModel> models, IEquipmentDayPlanStore plans,
    IEnumerable<IEquipmentScheduleCompiler> compilers)
{
    public async Task<EquipmentDayPlan> PlanAsync(
        EquipmentPlanningTarget target, DateOnly deliveryDate, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var existing = await plans.FindAsync(target.AssetId, deliveryDate, cancellationToken).ConfigureAwait(false);
        if (existing is not null) { return existing; }
        var model = models.Single(item => item.EquipmentKind == target.EquipmentKind);
        var optimized = await model.OptimizeAsync(target, deliveryDate, now, cancellationToken).ConfigureAwait(false);
        ValidateSchedule(target, deliveryDate, optimized);
        var compiler = compilers.Single(item => item.IntegrationId == target.IntegrationId);
        var plan = new EquipmentDayPlan(target, deliveryDate, optimized.Schedule,
            optimized.TelemetryAtUtc, optimized.InitialSocPercent, optimized.OptimizationRunId,
            compiler.Compile(target, deliveryDate, optimized.Schedule), optimized.InitialStateBasis);
        if (await plans.TrySaveAsync(plan, cancellationToken).ConfigureAwait(false)) { return plan; }
        return await plans.FindAsync(target.AssetId, deliveryDate, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("planning-persistence-conflict");
    }

    private static void ValidateSchedule(EquipmentPlanningTarget target, DateOnly date, EquipmentPlanOptimization optimized)
    {
        var schedule = optimized.Schedule;
        var horizon = EmsPlanningTime.Horizon(date);
        if (schedule.AssetId != target.AssetId || schedule.MarketBidArea != target.MarketBidArea
            || schedule.Type != BatteryEms.Domain.ScheduleType.DayAhead
            || schedule.HorizonStart != horizon.Start || schedule.HorizonEnd != horizon.End
            || optimized.OptimizationRunId == Guid.Empty)
        { throw new InvalidOperationException("planning-model-output-identity-mismatch"); }
        var next = horizon.Start;
        foreach (var window in schedule.Windows)
        {
            if (window.Start != next || !double.IsFinite(window.TargetPowerKw))
            { throw new InvalidOperationException("planning-model-output-grid-invalid"); }
            next = window.End;
        }
    }
}

public static class EmsPlanningTime
{
    public static TimeZoneInfo Kyiv { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
    public static DateOnly LocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Kyiv).DateTime);
    public static (DateTimeOffset Start, DateTimeOffset End) Horizon(DateOnly date) =>
        (ToUtc(date, TimeOnly.MinValue), ToUtc(date.AddDays(1), TimeOnly.MinValue));
    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), Kyiv));
    public static bool IsPlanningDue(DateTimeOffset utc) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Kyiv).DateTime) >= new TimeOnly(15, 0);
}
