using BatteryEms.Domain;

namespace BatteryEms.Application.Planning;

public sealed record EquipmentPlanningTarget(
    string SiteId, string AssetId, string IntegrationId, string MarketBidArea,
    string PriceUnit, double ThroughputCostPerKwh = 0, string EquipmentKind = "battery",
    double? ReserveSocPercent = null);

// Execution timing and payload format belong to the integration, not the EMS.
public sealed record EquipmentPlanAction(
    string ActionId, DateTimeOffset ScheduledAtUtc, DateTimeOffset DeadlineUtc, string PayloadJson);

public sealed record EquipmentDayPlan(
    EquipmentPlanningTarget Target, DateOnly DeliveryDate, Schedule Schedule,
    DateTimeOffset TelemetryAtUtc, double? InitialSocPercent, Guid OptimizationRunId,
    IReadOnlyList<EquipmentPlanAction> Actions, string InitialStateBasis = "model-defined");

public interface IEquipmentScheduleCompiler
{
    string IntegrationId { get; }
    IReadOnlyList<EquipmentPlanAction> Compile(
        EquipmentPlanningTarget target, DateOnly deliveryDate, Schedule schedule);
}

public interface IEquipmentDayPlanStore
{
    Task<EquipmentDayPlan?> FindAsync(string assetId, DateOnly deliveryDate, CancellationToken cancellationToken);
    // Immutable insert; concurrent planners must use the winner's entire plan.
    Task<bool> TrySaveAsync(EquipmentDayPlan plan, CancellationToken cancellationToken);
}

public interface IEquipmentPlanExecutor
{
    string IntegrationId { get; }
    // Integrations own durable execution dedupe and reconciliation, including
    // uncertainty after a network failure. The EMS must not retry physical writes.
    Task ExecuteAsync(EquipmentDayPlan plan, EquipmentPlanAction action, CancellationToken cancellationToken);
}

// Physical models are selected independently from vendor integrations. A PV,
// generator or controllable load can supply its own optimization model without
// changing scheduling, durable plans or execution compilation.
public sealed record EquipmentPlanOptimization(Schedule Schedule,
    DateTimeOffset TelemetryAtUtc, double? InitialSocPercent, Guid OptimizationRunId,
    string InitialStateBasis = "model-defined");

public interface IEquipmentDayAheadPlanningModel
{
    string EquipmentKind { get; }
    Task<EquipmentPlanOptimization> OptimizeAsync(EquipmentPlanningTarget target,
        DateOnly deliveryDate, DateTimeOffset now, CancellationToken cancellationToken);
}
