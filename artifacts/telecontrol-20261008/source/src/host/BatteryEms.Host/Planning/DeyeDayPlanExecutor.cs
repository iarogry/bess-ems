using System.Text.Json;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Planning;
using BatteryEms.Application.Time;

namespace BatteryEms.Host.Planning;

public sealed class DeyeDayPlanExecutor(EmsPlanningOptions options,
    IDeyeDayAuthorizationStore authorizations, IActivationWriterSafetyStore safety,
    IDeyeDayWindowDispatcher dispatcher, IClock clock) : IEquipmentPlanExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    public string IntegrationId => "deye_cloud";

    public async Task ExecuteAsync(EquipmentDayPlan plan, EquipmentPlanAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(action);
        if (!options.ActivationEnabled || !options.DayAuthorizationsEnabled) { return; }
        var now = clock.UtcNow;
        if (now < action.ScheduledAtUtc || now >= action.DeadlineUtc) { return; }
        var authorization = await authorizations.FindAsync(plan.Target.SiteId, plan.DeliveryDate, cancellationToken).ConfigureAwait(false);
        if (authorization is null || authorization.Revoked || authorization.ExpiresAtUtc <= now
            || authorization.CreatedAtUtc > now || authorization.WriterOwnerId != options.WriterOwnerId) { return; }
        var payload = JsonSerializer.Deserialize<ShadowPlanSnapshot>(action.PayloadJson, JsonOptions)
            ?? throw new InvalidDataException("Deye plan payload is missing.");
        if (payload.SiteId != plan.Target.SiteId || payload.DeliveryDate != plan.DeliveryDate
            || ActivationPayloadIntegrity.ComputeHash(payload) != authorization.PayloadHash)
        { throw new InvalidOperationException("deye-day-saved-plan-approval-mismatch"); }
        // A daily permission does not extend a short writer lease. Acquire or
        // renew it for each window and bind the exact fence into the claim.
        var lease = await safety.TryAcquireLeaseAsync(plan.Target.SiteId, options.WriterOwnerId!, now,
            TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        if (!lease.Acquired || lease.Lease is null) { return; }
        var claimed = await authorizations.ClaimAsync(new(plan.Target.SiteId, plan.DeliveryDate,
            action.ActionId, authorization.PayloadHash, options.WriterOwnerId!, authorization.SafetyRevision,
            lease.Lease.FencingToken, clock.UtcNow), cancellationToken).ConfigureAwait(false);
        if (!claimed.Accepted || claimed.IsReplay || claimed.Claim is null) { return; }
        // A persisted claim is one-shot, including a process crash before
        // transport or an unverified response. The broker owns reconciliation.
        await dispatcher.DispatchAsync(claimed.Claim, cancellationToken).ConfigureAwait(false);
    }
}
