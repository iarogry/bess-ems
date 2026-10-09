using BatteryEms.Application.Orchestration;

namespace BatteryEms.Api.Contracts;

public sealed record AgentShadowMismatchResponse(
    string Path,
    string LegacyValue,
    string ShadowValue);

public sealed record AgentShadowComparisonResponse(
    Guid ComparisonId,
    Guid RunId,
    string SiteId,
    DateOnly DeliveryDate,
    DateTimeOffset ComparedAtUtc,
    bool IsEquivalent,
    bool LegacyPayloadReady,
    bool ShadowPayloadReady,
    IReadOnlyList<AgentShadowMismatchResponse> Mismatches)
{
    public static AgentShadowComparisonResponse From(ShadowPlanComparisonRecord comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return new AgentShadowComparisonResponse(
            comparison.ComparisonId,
            comparison.RunId,
            comparison.SiteId,
            comparison.DeliveryDate,
            comparison.ComparedAtUtc,
            comparison.IsEquivalent,
            comparison.LegacyPayloadReady,
            comparison.ShadowPayloadReady,
            comparison.Mismatches
                .Select(item => new AgentShadowMismatchResponse(
                    item.Path,
                    item.LegacyValue,
                    item.ShadowValue))
                .ToArray());
    }
}
