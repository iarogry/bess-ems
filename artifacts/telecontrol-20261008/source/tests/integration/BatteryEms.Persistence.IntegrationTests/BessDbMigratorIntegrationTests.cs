using System.Globalization;
using BatteryEms.Adapters.Persistence;
using BatteryEms.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BatteryEms.Persistence.IntegrationTests;

// RM-M2-MIG-04: integration tests for BessDbMigrator against a real
// Postgres. The unit tests in BatteryEms.Adapters.Persistence.Tests
// already pin the continuity-preflight rules; these tests cover the
// runtime properties that need a database:
//   * The DbUp run actually creates the schema and records the
//     0001-version in the __schema_versions journal.
//   * A second MigrateAsync call is a no-op (DbUp recognises the
//     journaled version and skips it).
//   * Two parallel MigrateAsync calls serialise via pg_advisory_lock
//     instead of racing the journal-INSERT and producing duplicates.
//
// Each test starts by dropping schema public so the migrator runs
// against a clean state regardless of which other test class touched
// the DB before. The shared compose.yml Postgres is serialised with
// the [Collection("Postgres")] marker on both this class and
// PersistenceRoundtripTests.
[Trait("Category", "Integration")]
[Collection("Postgres")]
public sealed class BessDbMigratorIntegrationTests : IAsyncLifetime
{
    private NpgsqlDataSource? _dataSource;
    private string? _connectionString;

    private static string Host => Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "127.0.0.1";
    private static int Port => int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var p) ? p : 5432;
    private static string Database => Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "bessems";
    private static string User => Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "bessems";
    private static string Password => Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "bessems";

    public Task InitializeAsync()
    {
        var options = PersistenceOptions.FromHostPort(Host, Port, Database, User, Password);
        _connectionString = options.ConnectionString;
        _dataSource = NpgsqlDataSource.Create(_connectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    [Fact]
    public async Task MigrateAsync_applies_0001_and_records_in_journal()
    {
        await ResetSchemaAsync(_dataSource!);
        var migrator = new BessDbMigrator(_dataSource!, _connectionString!, NullLogger<BessDbMigrator>.Instance);

        await migrator.MigrateAsync(CancellationToken.None);

        // The 0001 migration creates the bess-ems schema; assert via
        // the telemetry sentinel table that DbUp actually executed.
        Assert.True(await TableExistsAsync(_dataSource!, "telemetry"));

        // DbUp records each applied script in __schema_versions; the
        // resource basename ends with `0001_initial.sql` regardless of
        // assembly-namespace prefix, so the LIKE filter is robust.
        var journalRows = await CountJournalEntriesAsync(_dataSource!, "%0001_initial.sql");
        Assert.Equal(1, journalRows);
    }

    [Fact]
    public async Task MigrateAsync_is_idempotent_on_second_call()
    {
        await ResetSchemaAsync(_dataSource!);
        var migrator = new BessDbMigrator(_dataSource!, _connectionString!, NullLogger<BessDbMigrator>.Instance);

        await migrator.MigrateAsync(CancellationToken.None);
        var firstCount = await CountJournalEntriesAsync(_dataSource!, "%0001_initial.sql");

        // Second call must observe the existing journal entry and skip
        // re-applying 0001. Anything else would trip on CREATE TABLE
        // (the embedded SQL has no IF NOT EXISTS) and throw.
        await migrator.MigrateAsync(CancellationToken.None);
        var secondCount = await CountJournalEntriesAsync(_dataSource!, "%0001_initial.sql");

        Assert.Equal(1, firstCount);
        Assert.Equal(1, secondCount);
    }

    [Fact]
    public async Task Timescale_migration_is_recorded_and_keeps_plain_postgres_schema_usable()
    {
        await ResetSchemaAsync(_dataSource!);
        var migrator = new BessDbMigrator(_dataSource!, _connectionString!, NullLogger<BessDbMigrator>.Instance);

        await migrator.MigrateAsync(CancellationToken.None);

        var journalRows = await CountJournalEntriesAsync(_dataSource!, "%0005_timescale_telemetry_hypertable.sql");
        Assert.Equal(1, journalRows);
        Assert.True(await TableExistsAsync(_dataSource!, "telemetry"));

        var telemetry = new DapperTelemetryRepository(_dataSource!);
        await telemetry.AppendAsync(new BatteryTelemetry(
            Timestamp: new DateTimeOffset(2026, 5, 13, 12, 0, 0, TimeSpan.Zero),
            AssetId: "timescale-plain-postgres",
            SocPercent: 50,
            SohPercent: 99,
            ActivePowerKw: 0,
            ReactivePowerKvar: 0,
            DcVoltage: 800,
            DcCurrent: 0,
            TemperatureCelsius: 22,
            Available: true,
            FaultStatus: "ok",
            DataQuality: DataQuality.Valid),
            CancellationToken.None);

        var latest = await telemetry.FindLatestAsync("timescale-plain-postgres", CancellationToken.None);
        Assert.NotNull(latest);
    }

    [Fact]
    public async Task Two_parallel_MigrateAsync_calls_serialize_via_advisory_lock()
    {
        await ResetSchemaAsync(_dataSource!);

        // Two distinct migrators (independent NpgsqlConnections from the
        // same DataSource) start in parallel. Without the advisory
        // lock, both would race past the journal-empty check and try
        // to CREATE TABLE telemetry, the loser raising "relation
        // already exists". With the lock, the second migrator waits
        // for the first to finish, then sees the journal entry and
        // exits cleanly.
        var migrator = new BessDbMigrator(_dataSource!, _connectionString!, NullLogger<BessDbMigrator>.Instance);

        var taskA = Task.Run(() => migrator.MigrateAsync(CancellationToken.None));
        var taskB = Task.Run(() => migrator.MigrateAsync(CancellationToken.None));

        await Task.WhenAll(taskA, taskB);

        var journalRows = await CountJournalEntriesAsync(_dataSource!, "%0001_initial.sql");
        Assert.Equal(1, journalRows);
        Assert.True(await TableExistsAsync(_dataSource!, "telemetry"));
    }

    [Fact]
    public async Task Window_scope_upgrade_guard_preserves_existing_pilot_history()
    {
        await ResetSchemaAsync(_dataSource!);
        await ExecuteSqlAsync("CREATE TABLE activation_pilot_sessions (audit_marker INTEGER); INSERT INTO activation_pilot_sessions VALUES (73);");

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteSqlAsync(ReadMigration("0020_activation_pilot_window_scope.sql")));

        Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        Assert.Contains("existing pilot history", error.MessageText, StringComparison.Ordinal);
        await using var command = _dataSource!.CreateCommand("SELECT audit_marker FROM activation_pilot_sessions;");
        Assert.Equal(73, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Claimed")]
    public async Task Window_scope_upgrade_guard_preserves_released_outbox(string status)
    {
        await ResetSchemaAsync(_dataSource!);
        await ExecuteSqlAsync("""
            CREATE TABLE activation_pilot_sessions (audit_marker INTEGER);
            CREATE TABLE activation_outbox (status TEXT, release_pilot_session_id UUID, claim_id UUID);
            """);
        await using (var insert = _dataSource!.CreateCommand("INSERT INTO activation_outbox(status) VALUES (@status);"))
        {
            insert.Parameters.AddWithValue("status", status);
            await insert.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteSqlAsync(ReadMigration("0020_activation_pilot_window_scope.sql")));

        Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        Assert.Contains("rollback all released or claimed", error.MessageText, StringComparison.Ordinal);
        await using var command = _dataSource!.CreateCommand("SELECT status FROM activation_outbox;");
        Assert.Equal(status, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("raw adapter error", 0)]
    [InlineData("test-bounded-result", -1)]
    public async Task Completion_evidence_upgrade_refuses_invalid_history_without_rewriting_it(string code, int offsetSeconds)
    {
        await ResetSchemaAsync(_dataSource!);
        await ExecuteSqlAsync("""
            CREATE TABLE activation_outbox (
                claim_id UUID, claim_outcome_code TEXT,
                claim_completed_at_utc TIMESTAMPTZ, claimed_at_utc TIMESTAMPTZ);
            """);
        var claimed = new DateTimeOffset(2026, 9, 22, 20, 55, 25, TimeSpan.Zero);
        await using (var insert = _dataSource!.CreateCommand("""
            INSERT INTO activation_outbox VALUES (@id, @code, @completed, @claimed);
            """))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("code", code);
            insert.Parameters.AddWithValue("completed", claimed.AddSeconds(offsetSeconds));
            insert.Parameters.AddWithValue("claimed", claimed);
            await insert.ExecuteNonQueryAsync();
        }
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteSqlAsync(ReadMigration("0021_activation_completion_evidence.sql")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        await using var command = _dataSource!.CreateCommand("SELECT claim_outcome_code, claim_completed_at_utc FROM activation_outbox;");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(code, reader.GetString(0));
        Assert.Equal(claimed.AddSeconds(offsetSeconds).UtcDateTime, reader.GetDateTime(1));
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var command = _dataSource!.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static string ReadMigration(string filename)
    {
        var assembly = typeof(BessDbMigrator).Assembly;
        var name = Assert.Single(assembly.GetManifestResourceNames(),
            name => name.EndsWith("." + filename, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task ResetSchemaAsync(NpgsqlDataSource dataSource)
    {
        var connection = await dataSource.OpenConnectionAsync();
        await using (connection.ConfigureAwait(false))
        {
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "DROP SCHEMA public CASCADE; CREATE SCHEMA public;";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task<bool> TableExistsAsync(NpgsqlDataSource dataSource, string tableName)
    {
        var connection = await dataSource.OpenConnectionAsync();
        await using (connection.ConfigureAwait(false))
        {
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText =
                    "SELECT EXISTS (SELECT 1 FROM information_schema.tables "
                    + "WHERE table_schema = 'public' AND table_name = @name);";
                cmd.Parameters.AddWithValue("@name", tableName);
                var result = await cmd.ExecuteScalarAsync();
                return result is bool b && b;
            }
        }
    }

    private static async Task<long> CountJournalEntriesAsync(NpgsqlDataSource dataSource, string scriptNameLike)
    {
        var connection = await dataSource.OpenConnectionAsync();
        await using (connection.ConfigureAwait(false))
        {
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText =
                    "SELECT COUNT(*) FROM __schema_versions WHERE scriptname LIKE @pat;";
                cmd.Parameters.AddWithValue("@pat", scriptNameLike);
                var result = await cmd.ExecuteScalarAsync();
                return result is long l ? l : Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }
        }
    }
}
