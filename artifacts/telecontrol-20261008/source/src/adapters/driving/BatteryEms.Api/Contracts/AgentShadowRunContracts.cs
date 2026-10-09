using BatteryEms.Application.Orchestration;

namespace BatteryEms.Api.Contracts;

public sealed record AgentShadowRunResponse(
    Guid RunId,
    string SiteId,
    DateOnly DeliveryDate,
    OrchestrationRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    Uri ComparisonUrl)
{
    public static AgentShadowRunResponse From(OrchestrationRun run, DateOnly deliveryDate)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new AgentShadowRunResponse(
            run.RunId,
            run.SiteId,
            deliveryDate,
            run.Status,
            run.StartedAt,
            run.CompletedAt,
            new Uri(
                $"/agent/sites/{Uri.EscapeDataString(run.SiteId)}/shadow-comparisons/latest?deliveryDate={deliveryDate:yyyy-MM-dd}",
                UriKind.Relative));
    }
}
