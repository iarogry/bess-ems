using System.Globalization;

namespace BatteryEms.Application.Orchestration;

public interface IShadowRunTrigger
{
    Task<OrchestrationRun> StartAsync(
        string siteId,
        DateOnly deliveryDate,
        OrchestrationTriggerType triggerType,
        CancellationToken cancellationToken);
}

/// <summary>
/// Creates only the read-only day-ahead shadow run. The stable per-site/day
/// idempotency key makes retries converge on the original orchestration run.
/// </summary>
public sealed class DefaultShadowRunTrigger : IShadowRunTrigger
{
    private const string RunType = "day-ahead-shadow";
    private readonly IOrchestrationUseCase _orchestration;

    public DefaultShadowRunTrigger(IOrchestrationUseCase orchestration)
    {
        _orchestration = orchestration ?? throw new ArgumentNullException(nameof(orchestration));
    }

    public Task<OrchestrationRun> StartAsync(
        string siteId,
        DateOnly deliveryDate,
        OrchestrationTriggerType triggerType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        if (triggerType is not OrchestrationTriggerType.Manual
            and not OrchestrationTriggerType.Scheduled
            and not OrchestrationTriggerType.Retry)
        {
            throw new ArgumentOutOfRangeException(
                nameof(triggerType),
                triggerType,
                "Shadow runs support only manual, scheduled or retry triggers.");
        }

        var horizon = ShadowDeliveryTime.GetUtcHorizon(deliveryDate);
        var dateText = deliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return _orchestration.StartAsync(
            new OrchestrationStartCommand(
                siteId.Trim(),
                RunType,
                horizon.Start,
                horizon.End,
                triggerType,
                TriggerRef: dateText,
                IdempotencyKey: $"{RunType}:{siteId.Trim()}:{dateText}"),
            cancellationToken);
    }

}
