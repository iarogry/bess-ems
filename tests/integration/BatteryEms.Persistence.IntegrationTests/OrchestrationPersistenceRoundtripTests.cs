using BatteryEms.Adapters.Persistence;
using BatteryEms.Application.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BatteryEms.Persistence.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public sealed class OrchestrationPersistenceRoundtripTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 6, 10, 0, 0, TimeSpan.Zero);

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

    private static async Task TruncateOrchestrationAsync(NpgsqlDataSource dataSource)
    {
        var connection = await dataSource.OpenConnectionAsync();
        await using (connection.ConfigureAwait(false))
        {
            await using var cmd = new NpgsqlCommand(
                "TRUNCATE orchestration_steps, orchestration_runs, orchestration_locks, orchestration_data_balances;",
                connection);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
