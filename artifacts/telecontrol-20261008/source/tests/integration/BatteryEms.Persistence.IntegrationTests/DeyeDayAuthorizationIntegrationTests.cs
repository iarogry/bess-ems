using System.Text.Json;
using BatteryEms.Adapters.Persistence;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BatteryEms.Persistence.IntegrationTests;

[Collection("Postgres")]
public sealed class DeyeDayAuthorizationIntegrationTests : IAsyncLifetime
{
    private NpgsqlDataSource _source = null!;
    private static readonly DateTimeOffset PreparedAt = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly DeliveryDate = new(2026, 10, 9);
    private static readonly JsonSerializerOptions JsonOptions = new();

    public async Task InitializeAsync()
    {
        var options = PersistenceOptions.FromHostPort(Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "127.0.0.1",
            int.Parse(Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432", System.Globalization.CultureInfo.InvariantCulture),
            Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "bessems",
            Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "bessems",
            Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "bessems");
        _source = NpgsqlDataSource.Create(options.ConnectionString);
        await new BessDbMigrator(_source, options.ConnectionString, NullLogger<BessDbMigrator>.Instance).MigrateAsync(CancellationToken.None);
        await using var command = _source.CreateCommand("""
            TRUNCATE deye_day_window_claims, deye_day_authorizations, device_write_broker_attempts,
                device_write_broker_sites, activation_pilot_sessions, activation_writer_leases,
                activation_writer_fence_sequences, activation_writer_safety, activation_outbox,
                activation_proposals, shadow_plan_comparisons, shadow_plan_snapshots,
                orchestration_steps, orchestration_runs, orchestration_locks, orchestration_data_balances;
            """);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _source.DisposeAsync();

    [Fact]
    public async Task One_independent_day_approval_executes_four_distinct_broker_windows_after_restarts()
    {
        var authorization = await PrepareAuthorizationAsync();
        var driver = new RecordingDriver();
        foreach (var windowId in new[] { "Z1", "Z2", "Z3", "Z4" })
        {
            var request = await ClaimRequestAsync(authorization, windowId);
            Assert.False((await new DapperDeviceWriteBrokerAttemptStore(_source).BeginAsync(request, CancellationToken.None)).Accepted);
            var execution = new DeviceWriteBrokerExecutionUseCase(
                new DapperDeviceWriteBrokerAttemptStore(_source, true), new DapperDeviceWriteBrokerPlanResolver(_source, true),
                driver, new TestClock(request.NowUtc));
            var result = await execution.ExecuteAsync(request, CancellationToken.None);
            Assert.True(result.ObservationRecorded);
            Assert.Equal(DeviceWriteBrokerAttemptState.Verified, result.State);
            var replay = await new DapperDeyeDayAuthorizationStore(_source).ClaimAsync(new(request.SiteId,
                request.DeliveryDate, request.WindowId, request.PayloadHash, request.WriterOwnerId,
                request.SafetyRevision, request.FencingToken, request.NowUtc.AddSeconds(1)), CancellationToken.None);
            Assert.True(replay.IsReplay);
            Assert.Equal(request.ActivationClaimId, replay.Claim!.ClaimId);
            var replayResult = await execution.ExecuteAsync(request, CancellationToken.None);
            Assert.False(replayResult.DriverInvoked);
        }
        Assert.Equal(4, driver.Writes);
        Assert.Equal(authorization.AuthorizationId,
            (await new DapperDeyeDayAuthorizationStore(_source).FindAsync("daily-site", DeliveryDate, CancellationToken.None))!.AuthorizationId);
    }

    [Fact]
    public async Task Revoking_after_initiation_blocks_mutation_and_resolving_the_payload()
    {
        var authorization = await PrepareAuthorizationAsync();
        var request = await ClaimRequestAsync(authorization, "Z1");
        var broker = new DapperDeviceWriteBrokerAttemptStore(_source, true);
        Assert.True((await broker.BeginAsync(request, CancellationToken.None)).Accepted);
        Assert.True((await broker.MarkInitiatedAsync(request.AttemptId, request.WriterOwnerId, request.NowUtc, CancellationToken.None)).Accepted);
        Assert.True(await broker.CanSendAsync(request, CancellationToken.None));
        Assert.True(await new DapperDeyeDayAuthorizationStore(_source).RevokeAsync(authorization.AuthorizationId,
            "operator-b", "test revocation", request.NowUtc.AddSeconds(1), CancellationToken.None));
        var later = request with { NowUtc = request.NowUtc.AddSeconds(2) };
        Assert.False(await broker.CanSendAsync(later, CancellationToken.None));
        Assert.Null(await new DapperDeviceWriteBrokerPlanResolver(_source, true).ResolveAsync(later, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_outcome_survives_restart_and_blocks_the_next_daily_window()
    {
        var authorization = await PrepareAuthorizationAsync();
        var first = await ClaimRequestAsync(authorization, "Z1");
        var broker = new DapperDeviceWriteBrokerAttemptStore(_source, true);
        Assert.True((await broker.BeginAsync(first, CancellationToken.None)).Accepted);
        Assert.True((await broker.MarkInitiatedAsync(first.AttemptId, first.WriterOwnerId, first.NowUtc, CancellationToken.None)).Accepted);
        Assert.True((await broker.ObserveAsync(first.AttemptId, first.WriterOwnerId, DeviceWriteBrokerObservation.Unknown,
            first.NowUtc.AddSeconds(1), CancellationToken.None)).Accepted);
        var second = await ClaimRequestAsync(authorization, "Z2");
        var restarted = new DapperDeviceWriteBrokerAttemptStore(_source, true);
        var blocked = await restarted.BeginAsync(second, CancellationToken.None);
        Assert.False(blocked.Accepted);
        Assert.Equal("device-write-broker-unresolved-attempt", blocked.BlockingCode);
    }

    [Fact]
    public async Task Claims_are_bound_to_writer_fence_payload_and_trigger_time()
    {
        var authorization = await PrepareAuthorizationAsync();
        var timing = ActivationWindowTimingPolicy.Evaluate(DeliveryDate, "Z1", PreparedAt);
        var now = timing.ScheduledAtUtc!.Value.AddSeconds(1);
        var lease = await new DapperActivationWriterSafetyStore(_source).TryAcquireLeaseAsync("daily-site", "ems-owner", now,
            TimeSpan.FromMinutes(1), CancellationToken.None);
        var valid = new DeyeDayWindowClaimRequest("daily-site", DeliveryDate, "Z1", authorization.PayloadHash,
            "ems-owner", 2, lease.Lease!.FencingToken, now);
        var store = new DapperDeyeDayAuthorizationStore(_source);
        Assert.False((await store.ClaimAsync(valid with { NowUtc = PreparedAt }, CancellationToken.None)).Accepted);
        Assert.False((await store.ClaimAsync(valid with { FencingToken = valid.FencingToken + 1 }, CancellationToken.None)).Accepted);
        Assert.False((await store.ClaimAsync(valid with { WriterOwnerId = "wrong-owner" }, CancellationToken.None)).Accepted);
        Assert.False((await store.ClaimAsync(valid with { PayloadHash = new string('A', 64) }, CancellationToken.None)).Accepted);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            new DapperDeyeDayAuthorizationStore(_source).ClaimAsync(valid, CancellationToken.None)));
        var winner = Assert.Single(results, result => result.Accepted && !result.IsReplay);
        Assert.All(results, result => Assert.True(result.Accepted));
        Assert.All(results, result => Assert.Equal(winner.Claim!.ClaimId, result.Claim!.ClaimId));
        Assert.False((await store.ClaimAsync(valid with { FencingToken = valid.FencingToken + 1 }, CancellationToken.None)).Accepted);
    }

    private async Task<DeviceWriteBrokerBeginRequest> ClaimRequestAsync(DeyeDayAuthorization authorization, string windowId)
    {
        var timing = ActivationWindowTimingPolicy.Evaluate(DeliveryDate, windowId, PreparedAt);
        var now = timing.ScheduledAtUtc!.Value.AddSeconds(1);
        var lease = await new DapperActivationWriterSafetyStore(_source).TryAcquireLeaseAsync("daily-site", "ems-owner", now,
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(lease.Acquired);
        var claim = await new DapperDeyeDayAuthorizationStore(_source).ClaimAsync(new("daily-site", DeliveryDate,
            windowId, authorization.PayloadHash, "ems-owner", 2, lease.Lease!.FencingToken, now), CancellationToken.None);
        Assert.True(claim.Accepted);
        return new(claim.Claim!.ClaimId, "daily-site", DeliveryDate, windowId, authorization.PayloadHash,
            ActivationWriterAuthority.ProductAgent, "ems-owner", 2, lease.Lease.FencingToken, claim.Claim.ClaimId, now);
    }

    private async Task<DeyeDayAuthorization> PrepareAuthorizationAsync()
    {
        var run = new OrchestrationRun(Guid.NewGuid(), "daily-site", "day-ahead-shadow", PreparedAt,
            PreparedAt.AddDays(1), OrchestrationTriggerType.Manual, "2026-10-09", Guid.NewGuid().ToString("N"),
            OrchestrationRunStatus.Running, PreparedAt, null, "hash", null, null, null, "{}");
        await new DapperOrchestrationRunStore(_source).TryStartAsync(run, CancellationToken.None);
        var comparisonId = Guid.NewGuid();
        await new DapperShadowPlanComparisonStore(_source).SaveComparisonAsync(new(comparisonId, run.RunId,
            "daily-site", DeliveryDate, PreparedAt, true, true, true, []), CancellationToken.None);
        var payload = new ShadowPlanSnapshot("daily-site", DeliveryDate, true, [], Enumerable.Range(1, 4)
            .Select(index => new ShadowTouWindow($"Z{index}", Enumerable.Range(0, 6)
                .Select(hour => new ShadowTouInterval($"{hour:00}:00", true, false, false, 0, 30, 290)).ToArray())).ToArray());
        var proposal = new ActivationProposal(Guid.NewGuid(), comparisonId, run.RunId, "daily-site", DeliveryDate,
            ActivationPayloadIntegrity.ComputeHash(payload), JsonSerializer.Serialize(payload, JsonOptions), "operator-a",
            "test reviewed plan", PreparedAt, PreparedAt.AddMinutes(30), ActivationProposalStatus.Pending);
        var proposals = new DapperActivationProposalStore(_source);
        Assert.True((await proposals.CreateAsync(proposal, CancellationToken.None)).Created);
        Assert.True((await proposals.ApproveAndHoldAsync(proposal.ProposalId, "operator-b", "independent test review",
            PreparedAt.AddMinutes(1), CancellationToken.None)).Approved);
        var safety = new DapperActivationWriterSafetyStore(_source);
        var initial = ActivationSafetyState.InitialFailClosed("daily-site", PreparedAt, "operator-b", "synthetic setup");
        Assert.True(await safety.CompareExchangeStateAsync(initial, null, CancellationToken.None));
        Assert.True(await safety.CompareExchangeStateAsync(initial with { Revision = 2, KillSwitchEngaged = false,
            WriterAuthority = ActivationWriterAuthority.ProductAgent, LegacyWriterStoppedAtUtc = PreparedAt,
            LegacyStopEvidence = "synthetic test only", UpdatedAtUtc = PreparedAt.AddMinutes(1) }, 1, CancellationToken.None));
        var store = new DapperDeyeDayAuthorizationStore(_source);
        var request = new DeyeDayAuthorizationRequest(Guid.NewGuid(), proposal.ProposalId, "ems-owner", 2,
            "operator-b", "authorize the reviewed delivery day", PreparedAt.AddMinutes(2));
        Assert.False((await store.AuthorizeAsync(request with { Actor = "operator-a" }, CancellationToken.None)).Accepted);
        var authorized = await store.AuthorizeAsync(request, CancellationToken.None);
        Assert.True(authorized.Accepted, authorized.BlockingCode);
        return authorized.Authorization!;
    }

    private sealed class TestClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
    private sealed class RecordingDriver : IDeviceWriteBrokerDriver
    {
        public int Writes { get; private set; }
        public Task<bool> PreflightAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<DeviceWriteBrokerReadback> WriteOnceAndVerifyAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
        { Writes++; return Task.FromResult(DeviceWriteBrokerReadback.Matched); }
    }
}
