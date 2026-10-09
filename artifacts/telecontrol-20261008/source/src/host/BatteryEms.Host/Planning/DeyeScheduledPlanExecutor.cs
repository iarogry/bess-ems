using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Planning;
using BatteryEms.Application.Time;

namespace BatteryEms.Host.Planning;

public sealed record DeyeActivationBinding(Guid SessionId, Guid ClaimId, string ExecutorId);

public sealed class DeyeScheduledPlanExecutor(
    EmsPlanningOptions options, IActivationPilotSessionStore sessions, IActivationProposalStore proposals,
    IActivationDispatchExecutionUseCase execution, IClock clock) : IEquipmentPlanExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    public string IntegrationId => "deye_cloud";

    public async Task ExecuteAsync(EquipmentDayPlan plan, EquipmentPlanAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(action);
        if (!options.ActivationEnabled) { return; }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.Target.AssetId)));
        var path = Path.Combine(options.ActivationBindingsDirectory!, $"{key}-{plan.DeliveryDate:yyyy-MM-dd}-{action.ActionId}.json");
        if (!File.Exists(path)) { return; } // No approved/armed pilot: no claim and no broker call.
        using var stream = File.OpenRead(path);
        var binding = await JsonSerializer.DeserializeAsync<DeyeActivationBinding>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Activation binding is empty.");
        var session = await sessions.FindAsync(binding.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || !session.IsActiveAt(clock.UtcNow) || session.SiteId != plan.Target.SiteId
            || session.WindowId != action.ActionId) { return; }
        var proposal = await proposals.FindAsync(session.ProposalId, cancellationToken).ConfigureAwait(false);
        var snapshot = JsonSerializer.Deserialize<ShadowPlanSnapshot>(action.PayloadJson, JsonOptions)
            ?? throw new InvalidDataException("Deye payload is empty.");
        if (proposal?.Status != ActivationProposalStatus.Approved
            || proposal.DeliveryDate != plan.DeliveryDate || proposal.SiteId != plan.Target.SiteId
            || proposal.PayloadHash != ActivationPayloadIntegrity.ComputeHash(snapshot))
        { throw new InvalidOperationException("planning-activation-approved-payload-mismatch"); }
        // The existing durable claim and broker admission recheck safety, lease,
        // fencing, time and payload. Replays never send the device command again.
        await execution.ExecuteAsync(new ActivationDispatchExecutionRequest(new ActivationPrewriteClaimRequest(
            binding.ClaimId, session.OutboxItemId, binding.ExecutorId, session.WriterOwnerId,
            session.SafetyRevision, session.FencingToken, clock.UtcNow)), cancellationToken).ConfigureAwait(false);
    }
}
