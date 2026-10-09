using BatteryEms.Adapters.Persistence;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using System.Text.Json;

namespace BatteryEms.Persistence.IntegrationTests;

internal sealed class ReleasedPilotFixture(NpgsqlDataSource dataSource)
{
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private static readonly DateTimeOffset Now =
        new(2026, 9, 22, 20, 52, 0, TimeSpan.Zero);

    internal async Task<ReleasedPilot> CreateAsync()
    {
        var started = await SeedComparisonsAsync();
        var store = new DapperActivationProposalStore(_dataSource!);
        var proposal = new ActivationProposal(
            Guid.NewGuid(),
            started.Run.RunId,
            started.Run.RunId,
            "site-1",
            new DateOnly(2026, 9, 23),
            ActivationPayloadIntegrity.ComputeHash(ShadowPlan("site-1", 60_000)),
            JsonSerializer.Serialize(ShadowPlan("site-1", 60_000)),
            "operator-a",
            "integration test proposal",
            Now,
            Now.AddMinutes(30),
            ActivationProposalStatus.Pending);

        var first = await store.CreateAsync(proposal, CancellationToken.None);
        var duplicate = await store.CreateAsync(
            proposal with { ProposalId = Guid.NewGuid() },
            CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.Proposal!.ProposalId, duplicate.Proposal!.ProposalId);

        var selfApproval = await store.ApproveAndHoldAsync(
            proposal.ProposalId,
            "operator-a",
            "must not self-approve",
            Now.AddMinutes(1),
            CancellationToken.None);
        Assert.False(selfApproval.Approved);
        Assert.Equal("activation-four-eyes-required", selfApproval.ErrorCode);

        var approved = await store.ApproveAndHoldAsync(
            proposal.ProposalId,
            "operator-b",
            "independent review complete",
            Now.AddMinutes(2),
            CancellationToken.None);
        Assert.True(approved.Approved);
        Assert.Equal(ActivationProposalStatus.Approved, approved.Proposal!.Status);
        Assert.Equal(ActivationOutboxStatus.Held, approved.OutboxItem!.Status);

        var repeated = await store.ApproveAndHoldAsync(
            proposal.ProposalId,
            "operator-b",
            "idempotent retry",
            Now.AddMinutes(3),
            CancellationToken.None);
        Assert.True(repeated.Approved);
        Assert.Equal(approved.OutboxItem.OutboxItemId, repeated.OutboxItem!.OutboxItemId);

        var reloaded = await new DapperActivationProposalStore(_dataSource!).FindAsync(
            proposal.ProposalId,
            CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(ActivationProposalStatus.Approved, reloaded!.Status);
        Assert.Equal("operator-b", reloaded.ReviewedBy);
        Assert.Equal(approved.OutboxItem.OutboxItemId, reloaded.OutboxItemId);
        Assert.Equal(1, await CountRowsAsync(_dataSource!, "activation_proposals"));
        Assert.Equal(1, await CountRowsAsync(_dataSource!, "activation_outbox"));

        var safetyStore = new DapperActivationWriterSafetyStore(_dataSource!);
        var initialSafety = ActivationSafetyState.InitialFailClosed(
            "site-1",
            Now.AddMinutes(3),
            "operator-a",
            "initialize cutover controls");
        Assert.True(await safetyStore.CompareExchangeStateAsync(
            initialSafety,
            null,
            CancellationToken.None));
        var cutoverSafety = initialSafety with
        {
            KillSwitchEngaged = false,
            WriterAuthority = ActivationWriterAuthority.ProductAgent,
            LegacyWriterStoppedAtUtc = Now.AddMinutes(3),
            LegacyStopEvidence = "pilot-runbook-step-7",
            Revision = 2,
            UpdatedAtUtc = Now.AddMinutes(3).AddSeconds(1),
            UpdatedBy = "operator-b",
            Reason = "authorize product writer",
        };
        Assert.True(await safetyStore.CompareExchangeStateAsync(
            cutoverSafety,
            expectedRevision: 1,
            CancellationToken.None));
        var lease = await safetyStore.TryAcquireLeaseAsync(
            "site-1",
            "agent-instance-a",
            Now.AddMinutes(3),
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        Assert.True(lease.Acquired);

        var pilotStore = new DapperActivationPilotSessionStore(_dataSource!);
        var wrongFence = await pilotStore.ArmAsync(
            new ActivationPilotArmRequest(
                Guid.NewGuid(),
                proposal.ProposalId,
                "Z1",
                "agent-instance-a",
                ExpectedSafetyRevision: 2,
                ExpectedFencingToken: lease.Lease!.FencingToken + 1,
                "operator-c",
                "must fail closed",
                Now.AddMinutes(3).AddSeconds(10)),
            CancellationToken.None);
        Assert.False(wrongFence.Armed);
        Assert.Equal("activation-fencing-token-mismatch", wrongFence.ErrorCode);

        var pilotSessionId = Guid.NewGuid();
        var armed = await pilotStore.ArmAsync(
            new ActivationPilotArmRequest(
                pilotSessionId,
                proposal.ProposalId,
                "Z1",
                "agent-instance-a",
                ExpectedSafetyRevision: 2,
                ExpectedFencingToken: lease.Lease.FencingToken,
                "operator-c",
                "arm bounded pilot",
                Now.AddMinutes(3).AddSeconds(15)),
            CancellationToken.None);
        Assert.True(armed.Armed);
        Assert.Equal(ActivationPilotSessionStatus.Armed, armed.Session!.Status);
        Assert.Equal("Z1", armed.Session.WindowId);

        var cutoverStore = new DapperActivationCutoverStore(_dataSource!);
        var releaseRequest = new ActivationOutboxReleaseRequest(
            proposal.ProposalId,
            pilotSessionId,
            "agent-instance-a",
            ExpectedSafetyRevision: 2,
            ExpectedFencingToken: lease.Lease.FencingToken,
            "operator-d",
            "release approved pilot payload",
            Now.AddMinutes(3).AddSeconds(20));
        var released = await cutoverStore.ReleaseHeldAsync(
            releaseRequest,
            CancellationToken.None);
        var releaseReplay = await cutoverStore.ReleaseHeldAsync(
            releaseRequest,
            CancellationToken.None);
        Assert.True(released.Released);
        Assert.True(releaseReplay.Released);
        Assert.Equal(ActivationOutboxStatus.Ready, released.OutboxItem!.Status);
        Assert.Equal(2, released.OutboxItem.ReleaseSafetyRevision);
        Assert.Equal(lease.Lease.FencingToken, released.OutboxItem.ReleaseFencingToken);
        Assert.Equal(pilotSessionId, released.OutboxItem.ReleasePilotSessionId);
        Assert.Equal("Z1", released.OutboxItem.ReleaseWindowId);

        await AssertOutboxMutationRejectedAsync(
            released.OutboxItem.OutboxItemId,
            "UPDATE activation_outbox SET release_window_id = NULL WHERE outbox_item_id = @id;",
            "ck_activation_outbox_release_metadata");

        var approvedReplay = await new DapperActivationProposalStore(_dataSource!).ApproveAndHoldAsync(
            proposal.ProposalId,
            "operator-b",
            "approval replay after release",
            Now.AddMinutes(3).AddSeconds(30),
            CancellationToken.None);
        Assert.True(approvedReplay.Approved);
        Assert.Equal(ActivationOutboxStatus.Ready, approvedReplay.OutboxItem!.Status);
        Assert.Equal(lease.Lease.FencingToken, approvedReplay.OutboxItem.ReleaseFencingToken);
        Assert.Equal(pilotSessionId, approvedReplay.OutboxItem.ReleasePilotSessionId);
        Assert.Equal("Z1", approvedReplay.OutboxItem.ReleaseWindowId);

        var claimRequest = new ActivationPrewriteClaimRequest(
            Guid.NewGuid(),
            approved.OutboxItem.OutboxItemId,
            "executor-instance-a",
            "agent-instance-a",
            ExpectedSafetyRevision: 2,
            ExpectedFencingToken: lease.Lease.FencingToken,
            Now.AddMinutes(3).AddSeconds(25));
        return new ReleasedPilot(claimRequest, pilotSessionId);
    }

    private async Task<OrchestrationRunStartResult> SeedComparisonsAsync()
    {
        var runStore = new DapperOrchestrationRunStore(_dataSource!);
        var started = await runStore.TryStartAsync(
            SampleRun("activation-proposal-run") with
            {
                RunType = ShadowPlanComparisonModule.SupportedRunType,
                TriggerRef = "2026-09-23",
            },
            CancellationToken.None);
        var comparisonStore = new DapperShadowPlanComparisonStore(_dataSource!);
        await comparisonStore.SaveComparisonAsync(
            new ShadowPlanComparisonRecord(
                started.Run.RunId,
                started.Run.RunId,
                "site-1",
                new DateOnly(2026, 9, 23),
                Now,
                IsEquivalent: true,
                LegacyPayloadReady: true,
                ShadowPayloadReady: true,
                Mismatches: []),
            CancellationToken.None);
        for (var daysBack = 1; daysBack < 14; daysBack++)
        {
            var deliveryDate = new DateOnly(2026, 9, 23).AddDays(-daysBack);
            var evidenceRun = await runStore.TryStartAsync(
                SampleRun($"activation-evidence-{deliveryDate:yyyy-MM-dd}") with
                {
                    RunType = ShadowPlanComparisonModule.SupportedRunType,
                    TriggerRef = deliveryDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                },
                CancellationToken.None);
            await comparisonStore.SaveComparisonAsync(
                new ShadowPlanComparisonRecord(
                    evidenceRun.Run.RunId,
                    evidenceRun.Run.RunId,
                    "site-1",
                    deliveryDate,
                    Now.AddDays(-daysBack),
                    IsEquivalent: true,
                    LegacyPayloadReady: true,
                    ShadowPayloadReady: true,
                    Mismatches: []),
                CancellationToken.None);
        }

        return started;
    }
    private static OrchestrationRun SampleRun(string idempotencyKey) => new(
        Guid.NewGuid(),
        "site-1",
        "day-ahead",
        Now,
        Now.AddDays(1),
        OrchestrationTriggerType.Manual,
        TriggerRef: "test",
        idempotencyKey,
        OrchestrationRunStatus.Running,
        Now,
        CompletedAt: null,
        InputHash: "hash",
        OutputRef: null,
        ErrorCode: null,
        ErrorMessage: null,
        MetadataJson: "{}");

    private static ShadowPlanSnapshot ShadowPlan(string siteId, int powerWatts) =>
        new(
            siteId,
            new DateOnly(2026, 9, 23),
            true,
            [],
            Enumerable.Range(1, 4)
                .Select(index => new ShadowTouWindow(
                    $"Z{index}",
                    [
                        ShadowInterval("00:00", 0),
                        ShadowInterval("01:00", powerWatts),
                        ShadowInterval("02:00", 0),
                        ShadowInterval("03:00", 80_000),
                        ShadowInterval("04:00", 0),
                        ShadowInterval("05:00", 0),
                    ]))
                .ToArray());

    private static ShadowTouInterval ShadowInterval(string startTime, int powerWatts) =>
        new(startTime, true, false, powerWatts > 0, powerWatts, 30, 290);

    private static async Task<long> CountRowsAsync(NpgsqlDataSource dataSource, string tableName)
    {
        var connection = await dataSource.OpenConnectionAsync();
        await using (connection.ConfigureAwait(false))
        {
            var commandText = tableName switch
            {
                "activation_proposals" => "SELECT COUNT(*) FROM activation_proposals;",
                "activation_outbox" => "SELECT COUNT(*) FROM activation_outbox;",
                _ => throw new ArgumentOutOfRangeException(nameof(tableName)),
            };
            await using var command = new NpgsqlCommand(commandText, connection);
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
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

    internal sealed record ReleasedPilot(ActivationPrewriteClaimRequest Request, Guid SessionId);
}
