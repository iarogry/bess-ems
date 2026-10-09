using BatteryEms.Adapters.Persistence;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using System.Text.Json;

namespace BatteryEms.Persistence.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public sealed class ActivationDispatchPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 22, 20, 52, 0, TimeSpan.Zero);

    private NpgsqlDataSource? _dataSource;
    private string? _connectionString;

    private static string Host => Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "127.0.0.1";
    private static int Port => int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var p) ? p : 5432;
    private static string Database => Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "bessems";
    private static string User => Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "bessems";
    private static string Password => Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "bessems";

    public async Task InitializeAsync()
    {
        var options = PersistenceOptions.FromHostPort(Host, Port, Database, User, Password);
        _connectionString = options.ConnectionString;
        _dataSource = NpgsqlDataSource.Create(_connectionString);
        await new BessDbMigrator(
            _dataSource,
            _connectionString,
            NullLogger<BessDbMigrator>.Instance).MigrateAsync(CancellationToken.None);
        await TruncateOrchestrationAsync(_dataSource);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    private Task<ReleasedPilotFixture.ReleasedPilot> PrepareReleasedAsync() =>
        new ReleasedPilotFixture(_dataSource!).CreateAsync();

    [Fact]
    public async Task Activation_proposal_is_idempotent_requires_four_eyes_and_holds_one_durable_outbox_item()
    {
        var pilot = await PrepareReleasedAsync();
        var claimRequest = pilot.Request;
        var pilotSessionId = pilot.SessionId;
        var claimStore = new DapperActivationPrewriteClaimStore(_dataSource!);
        var safetyStore = new DapperActivationWriterSafetyStore(_dataSource!);
        var cutoverStore = new DapperActivationCutoverStore(_dataSource!);
        var pilotStore = new DapperActivationPilotSessionStore(_dataSource!);
        var earlyClaim = await claimStore.TryClaimAsync(
            claimRequest with { ClaimId = Guid.NewGuid(), NowUtc = Now.AddMinutes(2) },
            CancellationToken.None);
        var lateClaim = await claimStore.TryClaimAsync(
            claimRequest with { ClaimId = Guid.NewGuid(), NowUtc = Now.AddMinutes(18) },
            CancellationToken.None);
        Assert.False(earlyClaim.Claimed);
        Assert.Equal("activation-window-trigger-not-due", earlyClaim.ErrorCode);
        Assert.False(lateClaim.Claimed);
        Assert.Equal("activation-window-trigger-expired", lateClaim.ErrorCode);
        var claimed = await claimStore.TryClaimAsync(claimRequest, CancellationToken.None);
        var claimReplay = await claimStore.TryClaimAsync(claimRequest, CancellationToken.None);
        var conflictingClaim = await claimStore.TryClaimAsync(
            claimRequest with { ClaimId = Guid.NewGuid(), ExecutorId = "executor-instance-b" },
            CancellationToken.None);
        Assert.True(claimed.Claimed);
        Assert.True(claimReplay.Claimed);
        Assert.True(claimReplay.IsReplay);
        Assert.False(claimed.IsReplay);
        Assert.False(conflictingClaim.Claimed);
        Assert.Equal("activation-outbox-already-claimed", conflictingClaim.ErrorCode);
        Assert.Equal(ActivationPrewriteClaim.DefaultDuration,
            claimed.Claim!.ExpiresAtUtc - claimed.Claim.ClaimedAtUtc);
        Assert.Equal("Z1", claimed.Claim.WindowId);
        Assert.False(string.IsNullOrWhiteSpace(claimed.Claim.WindowPayloadHash));

        await AssertOutboxMutationRejectedAsync(
            claimed.Claim.OutboxItemId,
            "UPDATE activation_outbox SET claim_window_payload_hash = NULL WHERE outbox_item_id = @id;",
            "ck_activation_outbox_claim_metadata");
        await AssertOutboxMutationRejectedAsync(
            claimed.Claim.OutboxItemId,
            "UPDATE activation_outbox SET claim_window_id = 'Z2' WHERE outbox_item_id = @id;",
            "ck_activation_outbox_claim_window_binding");

        var rollbackOperationId = Guid.NewGuid();
        var rollbackRequest = new ActivationRollbackRequest(
            rollbackOperationId,
            "site-1",
            ExpectedSafetyRevision: 2,
            "operator-d",
            "rollback pilot before dispatch",
            Now.AddMinutes(3).AddSeconds(40));
        var rollback = await cutoverStore.RollbackAsync(rollbackRequest, CancellationToken.None);
        var rollbackReplay = await cutoverStore.RollbackAsync(rollbackRequest, CancellationToken.None);
        Assert.True(rollback.RolledBack);
        Assert.True(rollbackReplay.RolledBack);
        Assert.Equal(1, rollback.CancelledReadyItems);
        Assert.Equal(1, rollbackReplay.CancelledReadyItems);
        Assert.True(rollback.SafetyState!.KillSwitchEngaged);
        Assert.Equal(ActivationWriterAuthority.None, rollback.SafetyState.WriterAuthority);
        Assert.Equal(3, rollback.SafetyState.Revision);
        Assert.Null(await safetyStore.FindLeaseAsync("site-1", CancellationToken.None));
        var abortedPilot = await pilotStore.FindAsync(pilotSessionId, CancellationToken.None);
        Assert.Equal(ActivationPilotSessionStatus.Aborted, abortedPilot!.Status);
        var lateCompletion = await claimStore.CompleteAsync(
            new ActivationPrewriteCompletionRequest(
                claimRequest.ClaimId,
                claimRequest.OutboxItemId,
                claimRequest.ExecutorId,
                Succeeded: true,
                "must-not-complete-after-rollback",
                Now.AddMinutes(3).AddSeconds(50)),
            CancellationToken.None);
        Assert.False(lateCompletion.Completed);
        Assert.Equal("activation-prewrite-claim-mismatch", lateCompletion.ErrorCode);
    }

    [Theory]
    [InlineData(ActivationDispatchOutcome.Rejected, "activation-plan-dispatcher-not-configured", "Failed")]
    [InlineData(ActivationDispatchOutcome.Succeeded, "test-only-readback-matched", "Succeeded")]
    [InlineData(ActivationDispatchOutcome.Unknown, "activation-dispatch-outcome-unknown", "Failed")]
    public async Task Dispatch_completion_is_durable_and_restart_never_redispatches(
        ActivationDispatchOutcome outcome, string code, string status)
    {
        var pilot = await PrepareReleasedAsync();
        var dispatcher = new CountingDispatcher((envelope, token) => outcome switch
        {
            ActivationDispatchOutcome.Rejected => new FailClosedActivationPlanDispatcher().DispatchAsync(envelope, token),
            ActivationDispatchOutcome.Succeeded => Task.FromResult(new ActivationPlanDispatchResult(outcome, code)),
            _ => throw new InvalidOperationException("test-only-raw-adapter-error-must-not-be-persisted"),
        });
        var result = await ExecuteAsync(pilot.Request, dispatcher, CancellationToken.None);
        Assert.True(result.Claimed);
        Assert.True(result.DispatcherInvoked);
        Assert.True(result.CompletionRecorded);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.OutcomeCode);
        await AssertOutboxEvidenceAsync(pilot.Request, status, code, completed: true);

        var restart = await ExecuteAsync(pilot.Request, dispatcher, CancellationToken.None);
        Assert.False(restart.DispatcherInvoked);
        Assert.Equal("activation-outbox-not-ready", restart.OutcomeCode);
        Assert.Equal(1, dispatcher.Calls);
        await AssertOutboxEvidenceAsync(pilot.Request, status, code, completed: true);
        var store = new DapperActivationPrewriteClaimStore(_dataSource!);
        var exactReplay = await store.CompleteAsync(new ActivationPrewriteCompletionRequest(
            pilot.Request.ClaimId, pilot.Request.OutboxItemId, pilot.Request.ExecutorId,
            outcome == ActivationDispatchOutcome.Succeeded, code, pilot.Request.NowUtc.AddSeconds(1)), CancellationToken.None);
        var conflictingCompletion = await store.CompleteAsync(new ActivationPrewriteCompletionRequest(
            pilot.Request.ClaimId, pilot.Request.OutboxItemId, pilot.Request.ExecutorId,
            outcome != ActivationDispatchOutcome.Succeeded, "test-conflicting-result", pilot.Request.NowUtc.AddSeconds(2)), CancellationToken.None);
        Assert.True(exactReplay.Completed);
        Assert.False(conflictingCompletion.Completed);
        Assert.Equal("activation-prewrite-completion-conflict", conflictingCompletion.ErrorCode);
        await AssertOutboxEvidenceAsync(pilot.Request, status, code, completed: true);
    }

    [Fact]
    public async Task Cancellation_after_claim_requires_reconciliation_even_after_restart_and_expiry()
    {
        var pilot = await PrepareReleasedAsync();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new CountingDispatcher(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable after cancellation.");
        });
        var execution = ExecuteAsync(pilot.Request, dispatcher, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await AssertOutboxEvidenceAsync(pilot.Request, "Claimed", null, completed: false);

        var restart = await ExecuteAsync(pilot.Request, dispatcher, CancellationToken.None);
        Assert.True(restart.Claimed);
        Assert.False(restart.DispatcherInvoked);
        Assert.False(restart.CompletionRecorded);
        Assert.Equal(ActivationDispatchOutcome.Unknown, restart.Outcome);
        Assert.Equal("activation-dispatch-replay-reconciliation-required", restart.OutcomeCode);
        var expired = await ExecuteAsync(
            pilot.Request with { NowUtc = pilot.Request.NowUtc.AddSeconds(16) }, dispatcher, CancellationToken.None);
        Assert.False(expired.Claimed);
        Assert.Equal("activation-prewrite-claim-expired-rollback-required", expired.OutcomeCode);
        Assert.Equal(1, dispatcher.Calls);
        await AssertOutboxEvidenceAsync(pilot.Request, "Claimed", null, completed: false);
    }

    [Fact]
    public async Task Competing_executor_cannot_dispatch_while_first_execution_is_in_flight()
    {
        var pilot = await PrepareReleasedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new CountingDispatcher(async (_, token) =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(token);
            return new ActivationPlanDispatchResult(ActivationDispatchOutcome.Succeeded, "test-only-readback-matched");
        });
        var first = ExecuteAsync(pilot.Request, dispatcher, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var competing = await ExecuteAsync(pilot.Request with
            {
                ClaimId = Guid.NewGuid(), ExecutorId = "executor-instance-b",
            }, dispatcher, CancellationToken.None);
            Assert.False(competing.Claimed);
            Assert.False(competing.DispatcherInvoked);
            Assert.Equal("activation-outbox-already-claimed", competing.OutcomeCode);
            Assert.Equal(1, dispatcher.Calls);
        }
        finally { finish.TrySetResult(); }
        var result = await first;
        Assert.True(result.CompletionRecorded);
        await AssertOutboxEvidenceAsync(pilot.Request, "Succeeded", "test-only-readback-matched", completed: true);
    }

    [Fact]
    public async Task Rollback_during_dispatch_cannot_be_resurrected_by_late_success()
    {
        var pilot = await PrepareReleasedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new CountingDispatcher(async (_, token) =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(token);
            return new ActivationPlanDispatchResult(ActivationDispatchOutcome.Succeeded, "test-only-readback-matched");
        });
        var execution = ExecuteAsync(pilot.Request, dispatcher, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var rollback = await new DapperActivationCutoverStore(_dataSource!).RollbackAsync(
                new ActivationRollbackRequest(Guid.NewGuid(), "site-1", 2, "operator-d",
                    "test rollback while dispatch is in flight", pilot.Request.NowUtc.AddSeconds(1)), CancellationToken.None);
            Assert.True(rollback.RolledBack);
            Assert.Equal(1, rollback.CancelledReadyItems);
            Assert.True(rollback.SafetyState!.KillSwitchEngaged);
        }
        finally { finish.TrySetResult(); }
        var result = await execution;
        Assert.Equal(ActivationDispatchOutcome.Unknown, result.Outcome);
        Assert.False(result.CompletionRecorded);
        Assert.Equal("activation-prewrite-claim-mismatch", result.OutcomeCode);
        await using var command = _dataSource!.CreateCommand("SELECT status, claim_id, claim_outcome_code FROM activation_outbox WHERE outbox_item_id = @id;");
        command.Parameters.AddWithValue("id", pilot.Request.OutboxItemId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Cancelled", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task Completion_rejects_unsafe_and_backdated_evidence_but_records_late_observation()
    {
        var pilot = await PrepareReleasedAsync();
        var store = new DapperActivationPrewriteClaimStore(_dataSource!);
        var claim = await store.TryClaimAsync(pilot.Request, CancellationToken.None);
        Assert.True(claim.Claimed);
        var completion = new ActivationPrewriteCompletionRequest(
            pilot.Request.ClaimId, pilot.Request.OutboxItemId, pilot.Request.ExecutorId,
            false, "test-only-late-outcome", pilot.Request.NowUtc.AddSeconds(45));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAsync(
            completion with { OutcomeCode = "raw adapter error containing secret=example" }, CancellationToken.None));
        var backdated = await store.CompleteAsync(
            completion with { NowUtc = pilot.Request.NowUtc.AddSeconds(-1) }, CancellationToken.None);
        Assert.False(backdated.Completed);
        Assert.Equal("activation-prewrite-completion-before-claim", backdated.ErrorCode);
        await AssertOutboxEvidenceAsync(pilot.Request, "Claimed", null, completed: false);

        await AssertOutboxMutationRejectedAsync(pilot.Request.OutboxItemId,
            """
            UPDATE activation_outbox SET status = 'Failed',
                claim_outcome_code = 'raw adapter error containing secret=example',
                claim_completed_at_utc = claimed_at_utc
            WHERE outbox_item_id = @id;
            """, "ck_activation_outbox_completion_evidence");
        await AssertOutboxMutationRejectedAsync(pilot.Request.OutboxItemId,
            """
            UPDATE activation_outbox SET status = 'Failed',
                claim_outcome_code = 'test-only-late-outcome',
                claim_completed_at_utc = claimed_at_utc - INTERVAL '1 second'
            WHERE outbox_item_id = @id;
            """, "ck_activation_outbox_completion_evidence");
        var late = await store.CompleteAsync(completion, CancellationToken.None);
        Assert.True(late.Completed);
        await AssertOutboxEvidenceAsync(pilot.Request, "Failed", completion.OutcomeCode,
            completed: true, completionAt: completion.NowUtc);
        var retry = await store.TryClaimAsync(pilot.Request with
        {
            ClaimId = Guid.NewGuid(), NowUtc = completion.NowUtc,
        }, CancellationToken.None);
        Assert.False(retry.Claimed);
        Assert.Equal("activation-outbox-not-ready", retry.ErrorCode);
    }

    private Task<ActivationDispatchExecutionResult> ExecuteAsync(
        ActivationPrewriteClaimRequest request, IActivationPlanDispatcher dispatcher, CancellationToken token) =>
        new DefaultActivationDispatchExecutionUseCase(
            new DapperActivationPrewriteClaimStore(_dataSource!), dispatcher, new FixedClock(request.NowUtc))
            .ExecuteAsync(new ActivationDispatchExecutionRequest(request), token);

    private async Task AssertOutboxEvidenceAsync(
        ActivationPrewriteClaimRequest request, string status, string? outcomeCode, bool completed,
        DateTimeOffset? completionAt = null)
    {
        await using var command = _dataSource!.CreateCommand("""
            SELECT status, claim_id, claim_executor_id, claim_window_id,
                   claim_outcome_code, claim_completed_at_utc, claimed_at_utc
            FROM activation_outbox WHERE outbox_item_id = @id;
            """);
        command.Parameters.AddWithValue("id", request.OutboxItemId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(status, reader.GetString(0));
        Assert.Equal(request.ClaimId, reader.GetGuid(1));
        Assert.Equal(request.ExecutorId, reader.GetString(2));
        Assert.Equal("Z1", reader.GetString(3));
        Assert.Equal(outcomeCode, reader.IsDBNull(4) ? null : reader.GetString(4));
        Assert.Equal(completed, !reader.IsDBNull(5));
        Assert.Equal(request.NowUtc.UtcDateTime, reader.GetDateTime(6));
        if (completed) { Assert.Equal((completionAt ?? request.NowUtc).UtcDateTime, reader.GetDateTime(5)); }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
    // Test-only dispatchers have no device/network dependency and are never
    // registered in the application host. Success here is not hardware evidence.
    private sealed class CountingDispatcher(
        Func<ActivationDispatchEnvelope, CancellationToken, Task<ActivationPlanDispatchResult>> dispatch) : IActivationPlanDispatcher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<ActivationPlanDispatchResult> DispatchAsync(ActivationDispatchEnvelope envelope, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            return dispatch(envelope, token);
        }
    }

    private async Task AssertOutboxMutationRejectedAsync(Guid outboxItemId, string sql, string constraint)
    {
        await using var command = _dataSource!.CreateCommand(sql);
        command.Parameters.AddWithValue("id", outboxItemId);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    private static async Task TruncateOrchestrationAsync(NpgsqlDataSource dataSource)
    {
        var connection = await dataSource.OpenConnectionAsync();
        await using (connection.ConfigureAwait(false))
        {
            await using var cmd = new NpgsqlCommand(
                "TRUNCATE deye_day_window_claims, deye_day_authorizations, device_write_broker_attempts, device_write_broker_sites, activation_pilot_sessions, activation_writer_leases, activation_writer_fence_sequences, activation_writer_safety, activation_outbox, activation_proposals, shadow_plan_comparisons, shadow_plan_snapshots, orchestration_steps, orchestration_runs, orchestration_locks, orchestration_data_balances;",
                connection);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
