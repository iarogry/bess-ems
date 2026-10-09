using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;

namespace BatteryEms.Host.Planning;

public sealed class HttpBrokerPlanDispatcher(HttpClient client, EmsPlanningOptions options, IClock clock)
    : IActivationPlanDispatcher, IDeyeDayWindowDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ActivationPlanDispatchResult> DispatchAsync(
        ActivationDispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.EnsureValid(clock.UtcNow);
        return await SendAsync(new BrokerRequest(envelope.Claim.ClaimId,
            envelope.Claim.SiteId, envelope.Plan.DeliveryDate, envelope.Window.WindowId,
            envelope.Claim.PayloadHash, envelope.Claim.SafetyRevision, envelope.Claim.FencingToken,
            envelope.Claim.ClaimId), cancellationToken).ConfigureAwait(false);
    }

    public Task<ActivationPlanDispatchResult> DispatchAsync(DeyeDayWindowClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var now = clock.UtcNow;
        if (claim.ClaimedAtUtc > now || claim.ExpiresAtUtc <= now
            || !ActivationWindowTimingPolicy.Evaluate(claim.DeliveryDate, claim.WindowId, now).CanStart)
        { throw new ArgumentException("Daily claim is outside its execution window.", nameof(claim)); }
        new DeviceWriteBrokerBeginRequest(claim.ClaimId, claim.SiteId, claim.DeliveryDate, claim.WindowId,
            claim.PayloadHash, ActivationWriterAuthority.ProductAgent, claim.WriterOwnerId,
            claim.SafetyRevision, claim.FencingToken, claim.ClaimId, now).EnsureValid();
        return SendAsync(new BrokerRequest(claim.ClaimId, claim.SiteId, claim.DeliveryDate, claim.WindowId,
            claim.PayloadHash, claim.SafetyRevision, claim.FencingToken, claim.ClaimId), cancellationToken);
    }

    private async Task<ActivationPlanDispatchResult> SendAsync(BrokerRequest body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BrokerBaseUrl!, "v1/device-writes"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.BrokerToken);
        request.Content = JsonContent.Create(body, options: JsonOptions);
        // No transport retries. The claim ID is also the broker attempt ID.
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        { return new(ActivationDispatchOutcome.Unknown, "activation-broker-http-outcome-unknown"); }
        var result = await response.Content.ReadFromJsonAsync<DeviceWriteBrokerExecutionResult>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return result is { State: DeviceWriteBrokerAttemptState.Verified }
            ? new(ActivationDispatchOutcome.Succeeded, "activation-broker-readback-verified")
            : new(ActivationDispatchOutcome.Unknown, "activation-broker-reconciliation-required");
    }

    private sealed record BrokerRequest(Guid AttemptId, string SiteId, DateOnly DeliveryDate,
        string WindowId, string PayloadHash, long SafetyRevision, long FencingToken, Guid ActivationClaimId);
}
