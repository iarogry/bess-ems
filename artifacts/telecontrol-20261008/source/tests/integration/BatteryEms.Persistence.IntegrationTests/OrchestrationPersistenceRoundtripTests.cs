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
public sealed class OrchestrationPersistenceRoundtripTests : IAsyncLifetime
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

    [Fact]
    public async Task Run_store_round_trips_run_and_steps_with_idempotency()
    {
        var store = new DapperOrchestrationRunStore(_dataSource!);
        var run = SampleRun("run-key");

        var first = await store.TryStartAsync(run, CancellationToken.None);
        var duplicate = await store.TryStartAsync(run with { RunId = Guid.NewGuid() }, CancellationToken.None);

        Assert.True(first.IsNewlyCreated);
        Assert.False(duplicate.IsNewlyCreated);
        Assert.Equal(first.Run.RunId, duplicate.Run.RunId);

        await store.AppendStepAsync(
            new OrchestrationStep(
                Guid.NewGuid(),
                first.Run.RunId,
                "site-1",
                "price_import_rdn",
                0,
                "step-key",
                OrchestrationStepStatus.Succeeded,
                Now,
                Now.AddSeconds(1),
                AttemptCount: 1,
                NextAttemptAt: null,
                InputHash: "hash",
                OutputRef: "price-series",
                DataRole.Critical,
                DataBalanceState.Ready,
                ErrorCode: null,
                ErrorMessage: null,
                MetadataJson: "{}"),
            CancellationToken.None);

        await store.CompleteAsync(
            first.Run.RunId,
            new OrchestrationRunCompletion(
                OrchestrationRunStatus.Succeeded,
                Now.AddSeconds(2),
                OutputRef: "ok"),
            CancellationToken.None);

        var stored = Assert.Single(await store.QueryCurrentAsync("site-1", CancellationToken.None));
        Assert.Equal(OrchestrationRunStatus.Succeeded, stored.Status);
        Assert.Equal("ok", stored.OutputRef);
        var step = Assert.Single(await store.QueryStepsAsync(first.Run.RunId, CancellationToken.None));
        Assert.Equal("price_import_rdn", step.Module);
        Assert.Equal(DataRole.Critical, step.DataRole);
        Assert.Equal(DataBalanceState.Ready, step.DataStatus);
    }

    [Fact]
    public async Task Lock_store_acquires_rejects_held_lock_reclaims_expired_and_releases_by_owner()
    {
        var store = new DapperOrchestrationLockStore(_dataSource!);
        var held = new OrchestrationLock("lock-1", "owner-1", Now, Now.AddMinutes(1), "{}");

        Assert.True(await store.TryAcquireAsync(held, Now, CancellationToken.None));
        Assert.False(await store.TryAcquireAsync(
            held with { OwnerId = "owner-2" },
            Now.AddSeconds(10),
            CancellationToken.None));

        Assert.True(await store.TryAcquireAsync(
            held with
            {
                OwnerId = "owner-3",
                AcquiredAt = Now.AddMinutes(2),
                ExpiresAt = Now.AddMinutes(3),
            },
            Now.AddMinutes(2),
            CancellationToken.None));

        await store.ReleaseAsync("lock-1", "wrong-owner", CancellationToken.None);
        Assert.False(await store.TryAcquireAsync(
            held with
            {
                OwnerId = "owner-4",
                AcquiredAt = Now.AddMinutes(2),
                ExpiresAt = Now.AddMinutes(4),
            },
            Now.AddMinutes(2),
            CancellationToken.None));

        await store.ReleaseAsync("lock-1", "owner-3", CancellationToken.None);
        Assert.True(await store.TryAcquireAsync(
            held with
            {
                OwnerId = "owner-4",
                AcquiredAt = Now.AddMinutes(2),
                ExpiresAt = Now.AddMinutes(4),
            },
            Now.AddMinutes(2),
            CancellationToken.None));
    }

    [Fact]
    public async Task Data_balance_store_upserts_and_queries_site_statuses()
    {
        var store = new DapperDataBalanceStore(_dataSource!);

        await store.UpsertAsync(
            SampleBalance("prices", DataRole.Critical, DataBalanceState.Missing),
            CancellationToken.None);
        await store.UpsertAsync(
            SampleBalance("prices", DataRole.Critical, DataBalanceState.Ready),
            CancellationToken.None);
        await store.UpsertAsync(
            SampleBalance("pv", DataRole.Advisory, DataBalanceState.SourceError) with { InstrumentId = "NE=129469793" },
            CancellationToken.None);

        var result = await store.QueryAsync("site-1", CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, balance => balance.DataGroup == "prices" && balance.Status == DataBalanceState.Ready);
        Assert.Contains(result, balance => balance.DataGroup == "pv" && balance.Status == DataBalanceState.SourceError);
    }

    [Fact]
    public async Task Shadow_store_persists_deduplicated_snapshots_and_idempotent_comparison()
    {
        var runStore = new DapperOrchestrationRunStore(_dataSource!);
        var started = await runStore.TryStartAsync(
            SampleRun("shadow-persistence-run") with
            {
                RunType = ShadowPlanComparisonModule.SupportedRunType,
                TriggerRef = "2026-09-23",
            },
            CancellationToken.None);
        var store = new DapperShadowPlanComparisonStore(_dataSource!);
        var legacy = ShadowPlan("site-1", powerWatts: 80_000);
        var shadow = ShadowPlan("site-1", powerWatts: 79_990);

        await store.PutSnapshotAsync(ShadowPlanSide.Legacy, legacy, CancellationToken.None);
        await store.PutSnapshotAsync(ShadowPlanSide.Legacy, legacy, CancellationToken.None);
        await store.PutSnapshotAsync(ShadowPlanSide.Shadow, shadow, CancellationToken.None);

        var reloadedStore = new DapperShadowPlanComparisonStore(_dataSource!);
        var storedLegacy = await reloadedStore.FindSnapshotAsync(
            ShadowPlanSide.Legacy,
            "site-1",
            new DateOnly(2026, 9, 23),
            CancellationToken.None);
        Assert.NotNull(storedLegacy);
        Assert.Equal(80_000, storedLegacy!.Windows[0].Intervals[1].PowerWatts);

        var comparison = new ShadowPlanComparisonRecord(
            started.Run.RunId,
            started.Run.RunId,
            "site-1",
            new DateOnly(2026, 9, 23),
            Now,
            IsEquivalent: false,
            LegacyPayloadReady: true,
            ShadowPayloadReady: true,
            [new ShadowPlanMismatch("windows[0].intervals[1].power_watts", "80000", "79990")]);
        await store.SaveComparisonAsync(comparison, CancellationToken.None);
        await store.SaveComparisonAsync(
            comparison with { ComparedAtUtc = Now.AddSeconds(1) },
            CancellationToken.None);

        var storedComparison = await reloadedStore.FindLatestComparisonAsync(
            "site-1",
            new DateOnly(2026, 9, 23),
            CancellationToken.None);
        Assert.NotNull(storedComparison);
        Assert.Equal(started.Run.RunId, storedComparison!.ComparisonId);
        Assert.Equal(Now.AddSeconds(1), storedComparison.ComparedAtUtc);
        Assert.Equal("windows[0].intervals[1].power_watts", Assert.Single(storedComparison.Mismatches).Path);
        Assert.Equal(2, await CountShadowSnapshotsAsync(_dataSource!));
        Assert.Equal(1, await CountShadowComparisonsAsync(_dataSource!));
    }

    [Fact]
    public async Task Writer_safety_state_is_versioned_and_lease_is_exclusive_with_monotonic_fencing()
    {
        var store = new DapperActivationWriterSafetyStore(_dataSource!);
        Assert.Null(await store.FindStateAsync("site-1", CancellationToken.None));

        var initial = ActivationSafetyState.InitialFailClosed(
            "site-1",
            Now,
            "operator-a",
            "initialize fail-closed controls");
        Assert.True(await store.CompareExchangeStateAsync(initial, null, CancellationToken.None));
        Assert.False(await store.CompareExchangeStateAsync(
            initial with { Revision = 2, Reason = "stale write" },
            expectedRevision: 0,
            CancellationToken.None));

        var cutover = initial with
        {
            KillSwitchEngaged = false,
            WriterAuthority = ActivationWriterAuthority.ProductAgent,
            LegacyWriterStoppedAtUtc = Now.AddMinutes(1),
            LegacyStopEvidence = "pilot-runbook-step-7",
            Revision = 2,
            UpdatedAtUtc = Now.AddMinutes(1),
            UpdatedBy = "operator-b",
            Reason = "controlled pilot cutover",
        };
        Assert.True(await store.CompareExchangeStateAsync(
            cutover,
            expectedRevision: 1,
            CancellationToken.None));

        var first = await store.TryAcquireLeaseAsync(
            "site-1",
            "agent-a",
            Now.AddMinutes(1),
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        var blocked = await store.TryAcquireLeaseAsync(
            "site-1",
            "agent-b",
            Now.AddMinutes(1).AddSeconds(10),
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        var successor = await store.TryAcquireLeaseAsync(
            "site-1",
            "agent-b",
            Now.AddMinutes(3),
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.True(first.Acquired);
        Assert.False(blocked.Acquired);
        Assert.True(successor.Acquired);
        Assert.True(successor.Lease!.FencingToken > first.Lease!.FencingToken);

        var reloaded = new DapperActivationWriterSafetyStore(_dataSource!);
        var storedState = await reloaded.FindStateAsync("site-1", CancellationToken.None);
        var storedLease = await reloaded.FindLeaseAsync("site-1", CancellationToken.None);
        var gate = ActivationWriterSafetyGate.Evaluate(
            "site-1",
            "agent-b",
            storedState,
            storedLease,
            Now.AddMinutes(3).AddSeconds(10));
        Assert.True(gate.CanWrite);
        Assert.Equal(successor.Lease.FencingToken, gate.FencingToken);
        Assert.Equal(2, gate.SafetyRevision);
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

    private static DataBalanceStatus SampleBalance(
        string group,
        DataRole role,
        DataBalanceState status) => new(
        "site-1",
        group,
        "test",
        group,
        role,
        status,
        FreshnessDeadlineUtc: Now.AddHours(1),
        LastSuccessAtUtc: status == DataBalanceState.Ready ? Now : null,
        LastAttemptAtUtc: Now,
        NextAttemptAtUtc: null,
        AttemptCount: 1,
        LastErrorCode: status == DataBalanceState.SourceError ? "source-error" : null,
        LastErrorMessage: null,
        QualitySummary: null,
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

    private static async Task<long> CountShadowSnapshotsAsync(NpgsqlDataSource dataSource)
    {
        var connection = await dataSource.OpenConnectionAsync();
        await using (connection.ConfigureAwait(false))
        {
            await using var command = new NpgsqlCommand(
                "SELECT COUNT(*) FROM shadow_plan_snapshots;",
                connection);
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }
    }

    private static async Task<long> CountShadowComparisonsAsync(NpgsqlDataSource dataSource)
    {
        var connection = await dataSource.OpenConnectionAsync();
        await using (connection.ConfigureAwait(false))
        {
            await using var command = new NpgsqlCommand(
                "SELECT COUNT(*) FROM shadow_plan_comparisons;",
                connection);
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }
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
