using BatteryEms.Api.Auth;
using BatteryEms.Application.Planning;

namespace BatteryEms.Host.Planning;

public static class EmsPlanningEndpoints
{
    public static void MapEmsPlanning(this WebApplication app, EmsPlanningOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) { return; }
        app.MapGet("/site/{siteId}/prepared-plans", async (
            string siteId, DateOnly? date, IEquipmentDayPlanStore plans, CancellationToken token) =>
        {
            var day = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
                DateTimeOffset.UtcNow, "Europe/Kyiv").DateTime);
            var result = new List<object>();
            foreach (var target in options.Targets.Where(target => target.SiteId == siteId))
            {
                var plan = await plans.FindAsync(target.AssetId, day, token).ConfigureAwait(false);
                if (plan is not null)
                {
                    result.Add(new { asset_id = target.AssetId, equipment_kind = target.EquipmentKind, delivery_date = plan.DeliveryDate,
                        schedule = plan.Schedule, telemetry_at_utc = plan.TelemetryAtUtc,
                        initial_soc_percent = plan.InitialSocPercent, optimization_run_id = plan.OptimizationRunId });
                }
            }
            return Results.Ok(new { site_id = siteId, delivery_date = day, plans = result,
                activation_enabled = options.ActivationEnabled });
        }).WithName("SitePreparedPlans").WithSummary("Read-only prepared schedules; does not activate equipment.");
        app.MapGet("/assets/{assetId}/day-ahead-plans/{deliveryDate}", async (
            string assetId, DateOnly deliveryDate, IEquipmentDayPlanStore plans, CancellationToken token) =>
        {
            var plan = await plans.FindAsync(assetId, deliveryDate, token).ConfigureAwait(false);
            return plan is null ? Results.NotFound() : Results.Ok(new
            {
                plan.Target, plan.DeliveryDate, plan.Schedule, plan.TelemetryAtUtc,
                plan.InitialSocPercent, plan.InitialStateBasis, plan.OptimizationRunId,
                actions = plan.Actions.Select(action => new { action.ActionId, action.ScheduledAtUtc, action.DeadlineUtc }),
            });
        }).RequireAuthorization(AuthConstants.AgentReadPolicy).WithName("GetPreparedEquipmentDayPlan");
    }
}
