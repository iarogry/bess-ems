using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Planning;
using BatteryEms.Application.Time;

namespace BatteryEms.Host.Planning;

public sealed partial class EmsPlanningHostedService(
    EmsPlanningOptions options, EmsDayAheadPlanner planner, IEquipmentDayPlanStore plans,
    IEnumerable<IEquipmentPlanExecutor> executors, IShadowRunTrigger shadow,
    IEnumerable<ILegacyPlanSnapshotSource> legacySources, IClock clock,
    ILogger<EmsPlanningHostedService> logger) : BackgroundService
{
    private readonly HashSet<string> _preparedOnStartup = new(StringComparer.Ordinal);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do { await RunOnceAsync(stoppingToken).ConfigureAwait(false); }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var target in options.Targets)
        {
            try
            {
                var now = clock.UtcNow;
                var today = EmsPlanningTime.LocalDate(now);
                if (EmsPlanningTime.IsPlanningDue(now)
                    || (options.PrepareOnStartup && !_preparedOnStartup.Contains(target.AssetId)))
                {
                    var plan = await planner.PlanAsync(target, today.AddDays(1), now, cancellationToken).ConfigureAwait(false);
                    _preparedOnStartup.Add(target.AssetId);
                    if (target.IntegrationId == "deye_cloud")
                    {
                        var legacySource = legacySources.SingleOrDefault();
                        if (legacySource is not null && (await legacySource.LoadAsync(target.SiteId,
                            plan.DeliveryDate, cancellationToken).ConfigureAwait(false)).Snapshot is { PayloadReady: true })
                        {
                            await shadow.StartAsync(target.SiteId, plan.DeliveryDate,
                                OrchestrationTriggerType.Scheduled, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
#pragma warning disable CA1031 // Isolate equipment failures; retry preparation when prices/telemetry arrive.
            catch (Exception exception) { Log.PlanningBlocked(logger, target.AssetId, exception); }
#pragma warning restore CA1031
            if (!options.ActivationEnabled) { continue; }
            // Planning errors must not prevent today's already-persisted actions.
            foreach (var date in new[] { EmsPlanningTime.LocalDate(clock.UtcNow), EmsPlanningTime.LocalDate(clock.UtcNow).AddDays(1) })
            {
                try
                {
                    var plan = await plans.FindAsync(target.AssetId, date, cancellationToken).ConfigureAwait(false);
                    if (plan is null) { continue; }
                    var executor = executors.Single(item => item.IntegrationId == plan.Target.IntegrationId);
                    foreach (var action in plan.Actions)
                    {
                        var now = clock.UtcNow;
                        if (now >= action.ScheduledAtUtc && now < action.DeadlineUtc)
                        { await executor.ExecuteAsync(plan, action, cancellationToken).ConfigureAwait(false); }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
#pragma warning disable CA1031 // A failed integration must not stop other equipment or automatic planning.
                catch (Exception exception) { Log.ExecutionBlocked(logger, target.AssetId, exception); }
#pragma warning restore CA1031
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(3101, LogLevel.Warning, "EMS day-ahead preparation blocked for {AssetId}")]
        public static partial void PlanningBlocked(ILogger logger, string assetId, Exception exception);
        [LoggerMessage(3102, LogLevel.Warning, "EMS scheduled action blocked for {AssetId}")]
        public static partial void ExecutionBlocked(ILogger logger, string assetId, Exception exception);
    }
}
