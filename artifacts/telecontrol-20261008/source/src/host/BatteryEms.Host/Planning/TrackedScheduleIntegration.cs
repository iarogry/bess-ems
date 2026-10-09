using System.Text.Json;
using BatteryEms.Application.Markets;
using BatteryEms.Application.Planning;
using BatteryEms.Domain;

namespace BatteryEms.Host.Planning;

// Streaming API integrations consume an unrestricted EMS schedule through
// the existing tracker/control cycle. They have no Deye TOU slot limit.
public sealed class TrackedScheduleIntegration(string integrationId, IScheduleRepository schedules)
    : IEquipmentScheduleCompiler, IEquipmentPlanExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    public string IntegrationId => integrationId;

    public IReadOnlyList<EquipmentPlanAction> Compile(
        EquipmentPlanningTarget target, DateOnly deliveryDate, Schedule schedule)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(schedule);
        return [new("activate-schedule", schedule.HorizonStart, schedule.HorizonEnd,
            JsonSerializer.Serialize(schedule, JsonOptions))];
    }

    public Task ExecuteAsync(EquipmentDayPlan plan, EquipmentPlanAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        var current = schedules.FindActive(plan.Target.AssetId, ScheduleType.DayAhead);
        if (current?.HorizonStart >= plan.Schedule.HorizonStart) { return Task.CompletedTask; }
        var schedule = new Schedule(plan.Schedule.AssetId, plan.Schedule.Type, plan.Schedule.MarketBidArea,
            (current?.Version ?? 0) + 1, plan.Schedule.Windows);
        schedules.Replace(schedule, current?.Version ?? 0);
        return Task.CompletedTask;
    }
}
