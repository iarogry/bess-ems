using System.Globalization;
using System.Text.Json;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Planning;

namespace BatteryEms.Host.Planning;

public sealed class PreparedDeyeShadowModule(EmsPlanningOptions options,
    IEquipmentDayPlanStore plans, IShadowPlanComparisonStore comparisons) : IOrchestrationModule
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    public string ModuleName => "schedule_shadow_plan_projection";
    public int StepOrder => 95;

    public async Task<OrchestrationModuleResult> ExecuteAsync(OrchestrationModuleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RunType != ShadowPlanComparisonModule.SupportedRunType)
        { return new(OrchestrationStepStatus.Skipped); }
        var target = options.Targets.SingleOrDefault(item => item.SiteId == context.SiteId && item.IntegrationId == "deye_cloud");
        if (target is null || !DateOnly.TryParseExact(context.TriggerRef, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        { return new(OrchestrationStepStatus.Blocked, ErrorCode: "planning-shadow-target-missing"); }
        var plan = await plans.FindAsync(target.AssetId, date, cancellationToken).ConfigureAwait(false);
        if (plan is null) { return new(OrchestrationStepStatus.Blocked, ErrorCode: "planning-day-plan-missing"); }
        var snapshot = JsonSerializer.Deserialize<ShadowPlanSnapshot>(plan.Actions[0].PayloadJson, JsonOptions)
            ?? throw new InvalidDataException("Prepared Deye payload is empty.");
        await comparisons.PutSnapshotAsync(ShadowPlanSide.Shadow, snapshot, cancellationToken).ConfigureAwait(false);
        return new(OrchestrationStepStatus.Succeeded);
    }
}
