namespace BatteryEms.Application.Orchestration;

public sealed record ActivationWindowTimingResult(
    bool CanStart,
    DateTimeOffset? ScheduledAtUtc,
    DateTimeOffset? StartDeadlineUtc,
    string? BlockingCode = null);

public static class ActivationWindowTimingPolicy
{
    public static readonly TimeSpan MaximumStartDelay = TimeSpan.FromMinutes(15);

    public static ActivationWindowTimingResult Evaluate(
        DateOnly deliveryDate,
        string windowId,
        DateTimeOffset nowUtc)
    {
        if (!ActivationPayloadIntegrity.IsWindowIdValid(windowId))
        {
            return new(false, null, null, "activation-window-id-invalid");
        }

        var horizon = ShadowDeliveryTime.GetUtcHorizon(deliveryDate);
        if (horizon.End - horizon.Start != TimeSpan.FromHours(24))
        {
            return new(false, null, null, "activation-dst-day-unsupported");
        }

        var triggerDate = windowId == "Z1" ? deliveryDate.AddDays(-1) : deliveryDate;
        var triggerTime = windowId switch
        {
            "Z1" => new TimeOnly(23, 55),
            "Z2" => new TimeOnly(5, 55),
            "Z3" => new TimeOnly(11, 55),
            _ => new TimeOnly(17, 55),
        };
        var scheduled = ShadowDeliveryTime.ToUtc(triggerDate, triggerTime);
        var deadline = scheduled.Add(MaximumStartDelay);
        nowUtc = nowUtc.ToUniversalTime();
        return new(
            nowUtc >= scheduled && nowUtc < deadline,
            scheduled,
            deadline,
            nowUtc < scheduled
                ? "activation-window-trigger-not-due"
                : nowUtc >= deadline ? "activation-window-trigger-expired" : null);
    }
}
