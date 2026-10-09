using BatteryEms.Application.Orchestration;

namespace BatteryEms.Api.Contracts;

public sealed record AgentPilotReadinessDayResponse(
    DateOnly DeliveryDate,
    bool EvidencePresent,
    bool Passed,
    string? BlockingCode);

public sealed record AgentPilotReadinessResponse(
    string SiteId,
    DateOnly WindowStart,
    DateOnly WindowEnd,
    int RequiredDays,
    int PassingDays,
    bool IsReady,
    IReadOnlyList<string> BlockingCodes,
    IReadOnlyList<AgentPilotReadinessDayResponse> Days)
{
    public static AgentPilotReadinessResponse From(PilotReadinessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new AgentPilotReadinessResponse(
            result.SiteId,
            result.WindowStart,
            result.WindowEnd,
            result.RequiredDays,
            result.PassingDays,
            result.IsReady,
            result.BlockingCodes,
            result.Days.Select(day => new AgentPilotReadinessDayResponse(
                day.DeliveryDate,
                day.EvidencePresent,
                Passed: day.BlockingCode is null,
                day.BlockingCode)).ToArray());
    }
}
