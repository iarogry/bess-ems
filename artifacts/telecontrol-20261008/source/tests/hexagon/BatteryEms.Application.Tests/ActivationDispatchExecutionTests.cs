using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class ActivationDispatchExecutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 20, 57, 0, TimeSpan.Zero);

    [Fact]
    public async Task Default_dispatcher_rejects_without_side_effect_capability()
    {
        var dispatcher = new FailClosedActivationPlanDispatcher();
        var result = await dispatcher.DispatchAsync(Envelope(), CancellationToken.None);

        Assert.Equal(ActivationDispatchOutcome.Rejected, result.Outcome);
        Assert.Equal("activation-plan-dispatcher-not-configured", result.OutcomeCode);
    }

    [Fact]
    public void Envelope_rejects_plan_object_that_differs_from_claimed_payload()
    {
        var envelope = Envelope();
        var changed = envelope.Plan with { DeliveryDate = envelope.Plan.DeliveryDate.AddDays(1) };

        var error = Assert.Throws<ArgumentException>(() =>
            new ActivationDispatchEnvelope(envelope.Claim, changed, envelope.Window).EnsureValid(Now));

        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Envelope_rejects_window_that_differs_from_claimed_scope()
    {
        var envelope = Envelope();
        var differentWindow = envelope.Plan.Windows.Single(window => window.WindowId == "Z2");

        var error = Assert.Throws<ArgumentException>(() =>
            new ActivationDispatchEnvelope(
                envelope.Claim,
                envelope.Plan,
                differentWindow).EnsureValid(Now));

        Assert.Contains("window does not match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execution_claims_dispatches_and_records_exact_success()
    {
        var store = new RecordingClaimStore(Claim());
        var dispatcher = new RecordingDispatcher(new(
            ActivationDispatchOutcome.Succeeded,
            "activation-vendor-readback-matched"));
        var sut = new DefaultActivationDispatchExecutionUseCase(store, dispatcher, new StubClock(Now));

        var result = await sut.ExecuteAsync(Request(), CancellationToken.None);

        Assert.True(result.Claimed);
        Assert.True(result.DispatcherInvoked);
        Assert.Equal(ActivationDispatchOutcome.Succeeded, result.Outcome);
        Assert.True(result.CompletionRecorded);
        Assert.NotNull(dispatcher.Envelope);
        Assert.True(store.Completion!.Succeeded);
        Assert.Equal("activation-vendor-readback-matched", store.Completion.OutcomeCode);
    }

    [Fact]
    public async Task Expired_claim_is_failed_without_invoking_dispatcher()
    {
        var store = new RecordingClaimStore(Claim(expiresAtUtc: Now));
        var dispatcher = new RecordingDispatcher(new(
            ActivationDispatchOutcome.Succeeded,
            "must-not-run"));
        var sut = new DefaultActivationDispatchExecutionUseCase(store, dispatcher, new StubClock(Now));

        var result = await sut.ExecuteAsync(Request(), CancellationToken.None);

        Assert.False(result.DispatcherInvoked);
        Assert.Null(dispatcher.Envelope);
        Assert.False(store.Completion!.Succeeded);
        Assert.Equal("activation-dispatch-claim-expired-or-invalid", result.OutcomeCode);
    }

    [Fact]
    public async Task Adapter_exception_is_terminal_unknown_and_not_retried()
    {
        var store = new RecordingClaimStore(Claim());
        var dispatcher = new ThrowingDispatcher();
        var sut = new DefaultActivationDispatchExecutionUseCase(store, dispatcher, new StubClock(Now));

        var result = await sut.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal(1, dispatcher.CallCount);
        Assert.Equal(ActivationDispatchOutcome.Unknown, result.Outcome);
        Assert.True(result.CompletionRecorded);
        Assert.False(store.Completion!.Succeeded);
        Assert.Equal("activation-dispatch-outcome-unknown", store.Completion.OutcomeCode);
    }

    [Fact]
    public async Task Unsafe_adapter_detail_is_replaced_with_bounded_unknown_code()
    {
        var store = new RecordingClaimStore(Claim());
        var dispatcher = new RecordingDispatcher(new(
            ActivationDispatchOutcome.Rejected,
            "device MASTER at https://vendor.invalid returned secret=abc"));
        var sut = new DefaultActivationDispatchExecutionUseCase(store, dispatcher, new StubClock(Now));

        var result = await sut.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal(ActivationDispatchOutcome.Unknown, result.Outcome);
        Assert.Equal("activation-dispatch-outcome-unknown", result.OutcomeCode);
        Assert.Equal("activation-dispatch-outcome-unknown", store.Completion!.OutcomeCode);
    }

    private static ActivationDispatchExecutionRequest Request() => new(new ActivationPrewriteClaimRequest(
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("20000000-0000-0000-0000-000000000002"),
        "executor-a",
        "writer-a",
        4,
        7,
        Now));

    [Fact]
    public async Task Claim_replay_never_invokes_dispatcher_or_overwrites_completion()
    {
        var store = new RecordingClaimStore(Claim(), isReplay: true);
        var dispatcher = new RecordingDispatcher(new(
            ActivationDispatchOutcome.Succeeded,
            "must-not-run"));
        var sut = new DefaultActivationDispatchExecutionUseCase(store, dispatcher, new StubClock(Now));

        var result = await sut.ExecuteAsync(Request(), CancellationToken.None);

        Assert.True(result.Claimed);
        Assert.False(result.DispatcherInvoked);
        Assert.Null(dispatcher.Envelope);
        Assert.Null(store.Completion);
        Assert.Equal("activation-dispatch-replay-reconciliation-required", result.OutcomeCode);
    }

    [Fact]
    public void Claim_window_hash_cannot_authorize_content_absent_from_approved_plan()
    {
        var envelope = Envelope();
        var changed = envelope.Window with
        {
            Intervals = envelope.Window.Intervals.Select(interval =>
                interval with { SocPercent = 80 }).ToArray(),
        };
        var claim = envelope.Claim with
        {
            WindowPayloadHash = ActivationPayloadIntegrity.ComputeWindowHash(changed),
        };

        Assert.Throws<ArgumentException>(() =>
            new ActivationDispatchEnvelope(claim, envelope.Plan, changed).EnsureValid(Now));
    }

    private static ActivationDispatchEnvelope Envelope()
    {
        var claim = Claim();
        Assert.True(ActivationPayloadIntegrity.TryReadWindow(
            claim.PayloadJson,
            claim.PayloadHash,
            claim.WindowId,
            out var plan,
            out var window));
        return new ActivationDispatchEnvelope(claim, plan!, window!).EnsureValid(Now);
    }

    [Fact]
    public void Envelope_rejects_clock_rollback_before_claim_acquisition()
    {
        var envelope = Envelope();

        Assert.Throws<ArgumentException>(() => envelope.EnsureValid(Now.AddSeconds(-2)));
    }

    [Fact]
    public async Task Execution_uses_server_clock_instead_of_request_time()
    {
        var store = new RecordingClaimStore(Claim());
        var sut = new DefaultActivationDispatchExecutionUseCase(
            store, new FailClosedActivationPlanDispatcher(), new StubClock(Now));
        var request = Request();

        await sut.ExecuteAsync(request with
        {
            ClaimRequest = request.ClaimRequest with { NowUtc = Now.AddDays(1) },
        }, CancellationToken.None);

        Assert.Equal(Now, store.LastRequest!.NowUtc);
    }

    private static ActivationPrewriteClaim Claim(DateTimeOffset? expiresAtUtc = null)
    {
        var plan = new ShadowPlanSnapshot(
            "site-a",
            new DateOnly(2026, 9, 29),
            PayloadReady: true,
            BlockingCodes: [],
            Windows:
            [
                Window("Z1"),
                Window("Z2"),
                Window("Z3"),
                Window("Z4"),
            ]).EnsureValid();
        var json = JsonSerializer.Serialize(plan);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var selectedWindow = plan.Windows.Single(window => window.WindowId == "Z1");
        return new ActivationPrewriteClaim(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            Guid.Parse("30000000-0000-0000-0000-000000000003"),
            "site-a",
            "Z1",
            ActivationPayloadIntegrity.ComputeWindowHash(selectedWindow),
            "executor-a",
            "writer-a",
            4,
            7,
            hash,
            json,
            Now.AddSeconds(-1),
            expiresAtUtc ?? Now.AddSeconds(14)).EnsureValid();
    }

    private static ShadowTouWindow Window(string id) => new(id,
    [
        new("00:00", true, false, false, 0, 30, 290),
        new("04:00", true, false, false, 0, 30, 290),
        new("08:00", true, false, false, 0, 30, 290),
        new("12:00", true, false, false, 0, 30, 290),
        new("16:00", true, false, false, 0, 30, 290),
        new("20:00", true, false, false, 0, 30, 290),
    ]);

    private sealed class RecordingClaimStore(ActivationPrewriteClaim claim, bool isReplay = false) : IActivationPrewriteClaimStore
    {
        public ActivationPrewriteCompletionRequest? Completion { get; private set; }
        public ActivationPrewriteClaimRequest? LastRequest { get; private set; }

        public Task<ActivationPrewriteClaimResult> TryClaimAsync(
            ActivationPrewriteClaimRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new ActivationPrewriteClaimResult(claim, true, IsReplay: isReplay));
        }

        public Task<ActivationPrewriteCompletionResult> CompleteAsync(
            ActivationPrewriteCompletionRequest request,
            CancellationToken cancellationToken)
        {
            Completion = request;
            return Task.FromResult(new ActivationPrewriteCompletionResult(
                request.Succeeded ? ActivationOutboxStatus.Succeeded : ActivationOutboxStatus.Failed,
                true));
        }
    }

    private sealed class RecordingDispatcher(ActivationPlanDispatchResult result) : IActivationPlanDispatcher
    {
        public ActivationDispatchEnvelope? Envelope { get; private set; }

        public Task<ActivationPlanDispatchResult> DispatchAsync(
            ActivationDispatchEnvelope envelope,
            CancellationToken cancellationToken)
        {
            Envelope = envelope;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingDispatcher : IActivationPlanDispatcher
    {
        public int CallCount { get; private set; }

        public Task<ActivationPlanDispatchResult> DispatchAsync(
            ActivationDispatchEnvelope envelope,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("simulated adapter failure");
        }
    }

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
