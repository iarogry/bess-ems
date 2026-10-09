using BatteryEms.Application.Time;

namespace BatteryEms.Application.Orchestration;

public enum ActivationDispatchOutcome
{
    Succeeded,
    Rejected,
    Unknown,
}

public sealed record ActivationDispatchEnvelope(
    ActivationPrewriteClaim Claim,
    ShadowPlanSnapshot Plan,
    ShadowTouWindow Window)
{
    public ActivationDispatchEnvelope EnsureValid(DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(Claim);
        ArgumentNullException.ThrowIfNull(Plan);
        ArgumentNullException.ThrowIfNull(Window);
        Claim.EnsureValid();
        Plan.EnsureValid();
        if (!string.Equals(Claim.SiteId, Plan.SiteId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Claim and plan site identities must match.");
        }

        if (!ActivationPayloadIntegrity.IsValid(Claim.PayloadJson, Claim.PayloadHash))
        {
            throw new ArgumentException("Claim payload integrity validation failed.");
        }

        if (!string.Equals(
                ActivationPayloadIntegrity.ComputeHash(Plan),
                Claim.PayloadHash,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Dispatch plan does not match the claimed payload.");
        }

        if (!string.Equals(Window.WindowId, Claim.WindowId, StringComparison.Ordinal)
            || !string.Equals(
                ActivationPayloadIntegrity.ComputeWindowHash(Window),
                Claim.WindowPayloadHash,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Dispatch window does not match the claimed window payload.");
        }

        var approvedWindow = Plan.Windows.Where(candidate =>
            string.Equals(candidate.WindowId, Claim.WindowId, StringComparison.Ordinal)).ToArray();
        if (approvedWindow.Length != 1
            || !string.Equals(
                ActivationPayloadIntegrity.ComputeWindowHash(approvedWindow[0]),
                Claim.WindowPayloadHash,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Claimed window is not bound to the approved plan.");
        }

        if (Claim.ClaimedAtUtc > nowUtc.ToUniversalTime()
            || Claim.ExpiresAtUtc <= nowUtc.ToUniversalTime())
        {
            throw new ArgumentException("Claim is not live at dispatch time.");
        }

        var timing = ActivationWindowTimingPolicy.Evaluate(Plan.DeliveryDate, Claim.WindowId, nowUtc);
        if (!timing.CanStart
            || Claim.ClaimedAtUtc < timing.ScheduledAtUtc
            || Claim.ExpiresAtUtc > timing.StartDeadlineUtc)
        {
            throw new ArgumentException(timing.BlockingCode ?? "activation-claim-outside-trigger-window");
        }

        return this;
    }
}

public sealed record ActivationPlanDispatchResult(
    ActivationDispatchOutcome Outcome,
    string OutcomeCode)
{
    public ActivationPlanDispatchResult EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OutcomeCode);
        if (OutcomeCode.Length > 96
            || OutcomeCode[0] == '-'
            || OutcomeCode[^1] == '-'
            || OutcomeCode.Any(character =>
                character != '-'
                && (character < 'a' || character > 'z')
                && (character < '0' || character > '9')))
        {
            throw new ArgumentException(
                "Outcome code must be a bounded lowercase technical code.",
                nameof(OutcomeCode));
        }

        return this;
    }
}

public interface IActivationPlanDispatcher
{
    Task<ActivationPlanDispatchResult> DispatchAsync(
        ActivationDispatchEnvelope envelope,
        CancellationToken cancellationToken);
}

public sealed class FailClosedActivationPlanDispatcher : IActivationPlanDispatcher
{
    public Task<ActivationPlanDispatchResult> DispatchAsync(
        ActivationDispatchEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ActivationPlanDispatchResult(
            ActivationDispatchOutcome.Rejected,
            "activation-plan-dispatcher-not-configured"));
    }
}

public sealed record ActivationDispatchExecutionRequest(
    ActivationPrewriteClaimRequest ClaimRequest);

public sealed record ActivationDispatchExecutionResult(
    bool Claimed,
    bool DispatcherInvoked,
    ActivationDispatchOutcome? Outcome,
    bool CompletionRecorded,
    string OutcomeCode);

public interface IActivationDispatchExecutionUseCase
{
    Task<ActivationDispatchExecutionResult> ExecuteAsync(
        ActivationDispatchExecutionRequest request,
        CancellationToken cancellationToken);
}

public sealed class DefaultActivationDispatchExecutionUseCase : IActivationDispatchExecutionUseCase
{
    private readonly IActivationPrewriteClaimStore _claimStore;
    private readonly IActivationPlanDispatcher _dispatcher;
    private readonly IClock _clock;

    public DefaultActivationDispatchExecutionUseCase(
        IActivationPrewriteClaimStore claimStore,
        IActivationPlanDispatcher dispatcher,
        IClock clock)
    {
        _claimStore = claimStore ?? throw new ArgumentNullException(nameof(claimStore));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "An adapter failure has an unknown device outcome and must be durably terminal rather than escaping into an automatic retry loop.")]
    public async Task<ActivationDispatchExecutionResult> ExecuteAsync(
        ActivationDispatchExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ClaimRequest);
        var claimResult = await _claimStore.TryClaimAsync(
            request.ClaimRequest with { NowUtc = _clock.UtcNow },
            cancellationToken).ConfigureAwait(false);
        if (!claimResult.Claimed || claimResult.Claim is null)
        {
            return new ActivationDispatchExecutionResult(
                Claimed: false,
                DispatcherInvoked: false,
                Outcome: null,
                CompletionRecorded: false,
                claimResult.ErrorCode ?? "activation-prewrite-claim-rejected");
        }

        if (claimResult.IsReplay)
        {
            return new ActivationDispatchExecutionResult(
                Claimed: true,
                DispatcherInvoked: false,
                ActivationDispatchOutcome.Unknown,
                CompletionRecorded: false,
                "activation-dispatch-replay-reconciliation-required");
        }

        var claim = claimResult.Claim.EnsureValid();
        if (!ActivationPayloadIntegrity.TryReadWindow(
                claim.PayloadJson,
                claim.PayloadHash,
                claim.WindowId,
                out var plan,
                out var window)
            || plan is null
            || window is null
            || !string.Equals(
                ActivationPayloadIntegrity.ComputeWindowHash(window),
                claim.WindowPayloadHash,
                StringComparison.Ordinal))
        {
            return await CompleteFailureAsync(
                claim,
                dispatcherInvoked: false,
                ActivationDispatchOutcome.Rejected,
                "activation-dispatch-payload-integrity-failed",
                cancellationToken).ConfigureAwait(false);
        }

        ActivationDispatchEnvelope envelope;
        try
        {
            envelope = new ActivationDispatchEnvelope(claim, plan, window).EnsureValid(_clock.UtcNow);
        }
        catch (ArgumentException)
        {
            return await CompleteFailureAsync(
                claim,
                dispatcherInvoked: false,
                ActivationDispatchOutcome.Rejected,
                "activation-dispatch-claim-expired-or-invalid",
                cancellationToken).ConfigureAwait(false);
        }

        ActivationPlanDispatchResult dispatch;
        try
        {
            dispatch = (await _dispatcher.DispatchAsync(envelope, cancellationToken)
                .ConfigureAwait(false)).EnsureValid();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await CompleteFailureAsync(
                claim,
                dispatcherInvoked: true,
                ActivationDispatchOutcome.Unknown,
                "activation-dispatch-outcome-unknown",
                cancellationToken).ConfigureAwait(false);
        }

        return await CompleteAsync(
            claim,
            dispatcherInvoked: true,
            dispatch,
            cancellationToken).ConfigureAwait(false);
    }

    private Task<ActivationDispatchExecutionResult> CompleteFailureAsync(
        ActivationPrewriteClaim claim,
        bool dispatcherInvoked,
        ActivationDispatchOutcome outcome,
        string outcomeCode,
        CancellationToken cancellationToken) => CompleteAsync(
            claim,
            dispatcherInvoked,
            new ActivationPlanDispatchResult(outcome, outcomeCode),
            cancellationToken);

    private async Task<ActivationDispatchExecutionResult> CompleteAsync(
        ActivationPrewriteClaim claim,
        bool dispatcherInvoked,
        ActivationPlanDispatchResult dispatch,
        CancellationToken cancellationToken)
    {
        var completion = await _claimStore.CompleteAsync(
            new ActivationPrewriteCompletionRequest(
                claim.ClaimId,
                claim.OutboxItemId,
                claim.ExecutorId,
                dispatch.Outcome == ActivationDispatchOutcome.Succeeded,
                dispatch.OutcomeCode,
                _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);
        if (!completion.Completed)
        {
            return new ActivationDispatchExecutionResult(
                Claimed: true,
                dispatcherInvoked,
                ActivationDispatchOutcome.Unknown,
                CompletionRecorded: false,
                completion.ErrorCode ?? "activation-dispatch-completion-unrecorded");
        }

        return new ActivationDispatchExecutionResult(
            Claimed: true,
            dispatcherInvoked,
            dispatch.Outcome,
            CompletionRecorded: true,
            dispatch.OutcomeCode);
    }
}
