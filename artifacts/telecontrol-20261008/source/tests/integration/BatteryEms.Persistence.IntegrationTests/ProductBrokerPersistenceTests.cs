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
public sealed class ProductBrokerPersistenceTests : IAsyncLifetime
{
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
    public async Task Broker_product_claim_is_exact_and_rollback_cannot_clear_initiated_latch()
    {
        var pilot = await PrepareReleasedAsync();
        var claimed = await new DapperActivationPrewriteClaimStore(_dataSource!).TryClaimAsync(pilot.Request, CancellationToken.None);
        Assert.True(claimed.Claimed);
        var claim = claimed.Claim!;
        var request = new DeviceWriteBrokerBeginRequest(Guid.NewGuid(), claim.SiteId,
            new DateOnly(2026, 9, 23), claim.WindowId, claim.PayloadHash,
            ActivationWriterAuthority.ProductAgent, claim.WriterOwnerId, claim.SafetyRevision,
            claim.FencingToken, claim.ClaimId, pilot.Request.NowUtc);
        var broker = new DapperDeviceWriteBrokerAttemptStore(_dataSource!);
        var invalid = await broker.BeginAsync(request with { ActivationClaimId = Guid.NewGuid() }, CancellationToken.None);
        Assert.False(invalid.Accepted);
        Assert.Equal("device-write-broker-product-claim-invalid", invalid.BlockingCode);
        var begun = await broker.BeginAsync(request, CancellationToken.None);
        Assert.True(begun.Accepted);
        Assert.False(begun.IsReplay);
        var initiated = await broker.MarkInitiatedAsync(request.AttemptId, request.WriterOwnerId, request.NowUtc, CancellationToken.None);
        Assert.True(initiated.Accepted);
        var rollback = await new DapperActivationCutoverStore(_dataSource!).RollbackAsync(
            new ActivationRollbackRequest(Guid.NewGuid(), "site-1", 2, "operator-d",
                "broker test rollback with possible in-flight device request", request.NowUtc.AddSeconds(1)), CancellationToken.None);
        Assert.True(rollback.RolledBack);
        var unknown = await broker.ObserveAsync(request.AttemptId, request.WriterOwnerId,
            DeviceWriteBrokerObservation.Unknown, request.NowUtc.AddSeconds(2), CancellationToken.None);
        Assert.True(unknown.Accepted);
        var competing = await new DapperDeviceWriteBrokerAttemptStore(_dataSource!).BeginAsync(
            request with { AttemptId = Guid.NewGuid(), NowUtc = request.NowUtc.AddMinutes(2) }, CancellationToken.None);
        Assert.False(competing.Accepted);
        Assert.Equal("device-write-broker-unresolved-attempt", competing.BlockingCode);
        var falseNotSent = await broker.ObserveAsync(request.AttemptId, request.WriterOwnerId,
            DeviceWriteBrokerObservation.NotSent, request.NowUtc.AddSeconds(3), CancellationToken.None);
        Assert.False(falseNotSent.Accepted);
        Assert.Equal("device-write-broker-reconciliation-required", falseNotSent.BlockingCode);
    }

    [Fact]
    public async Task Broker_rechecks_legacy_stop_evidence_at_product_network_start()
    {
        var pilot = await PrepareReleasedAsync();
        var claimed = await new DapperActivationPrewriteClaimStore(_dataSource!).TryClaimAsync(pilot.Request, CancellationToken.None);
        Assert.True(claimed.Claimed);
        var claim = claimed.Claim!;
        var request = new DeviceWriteBrokerBeginRequest(Guid.NewGuid(), claim.SiteId,
            new DateOnly(2026, 9, 23), claim.WindowId, claim.PayloadHash,
            ActivationWriterAuthority.ProductAgent, claim.WriterOwnerId, claim.SafetyRevision,
            claim.FencingToken, claim.ClaimId, pilot.Request.NowUtc);
        var broker = new DapperDeviceWriteBrokerAttemptStore(_dataSource!);
        Assert.True((await broker.BeginAsync(request, CancellationToken.None)).Accepted);
        // Older safety DDL allows NULL through its three-valued CHECK. The
        // broker must still reject this corrupted control-plane evidence.
        await using (var corrupt = _dataSource!.CreateCommand("UPDATE activation_writer_safety SET legacy_stop_evidence = NULL WHERE site_id = 'site-1';"))
        {
            await corrupt.ExecuteNonQueryAsync();
        }
        var start = await broker.MarkInitiatedAsync(request.AttemptId, request.WriterOwnerId, request.NowUtc, CancellationToken.None);
        Assert.False(start.Accepted);
        Assert.Equal("activation-legacy-writer-stop-unproven", start.BlockingCode);
    }

    [Theory]
    [InlineData(0)] // Exact approved plan verifies once.
    [InlineData(1)] // Rollback during preflight prevents the update.
    [InlineData(2)] // Rollback after update cannot recall or retry it.
    public async Task Broker_product_executor_preserves_one_update_boundary_through_rollback(int rollbackPhase)
    {
        var pilot = await PrepareReleasedAsync();
        var claimed = await new DapperActivationPrewriteClaimStore(_dataSource!).TryClaimAsync(pilot.Request, CancellationToken.None);
        Assert.True(claimed.Claimed);
        var claim = claimed.Claim!;
        Assert.True(ActivationPayloadIntegrity.TryReadSnapshot(claim.PayloadJson, claim.PayloadHash, out var plan));
        var request = new DeviceWriteBrokerBeginRequest(Guid.NewGuid(), claim.SiteId, plan!.DeliveryDate,
            claim.WindowId, claim.PayloadHash, ActivationWriterAuthority.ProductAgent, claim.WriterOwnerId,
            claim.SafetyRevision, claim.FencingToken, claim.ClaimId, pilot.Request.NowUtc);
        using var vendor = new DeviceWriteBrokerAttemptIntegrationTests.FakeVendor { MatchedReadback = rollbackPhase != 2 };
        using var client = new HttpClient(vendor);
        var driver = new DeviceWriteBrokerAttemptIntegrationTests.FakeDriver(client)
        {
            DuringPreflight = rollbackPhase == 1 ? () => RollbackAsync(claim.SiteId, request.NowUtc) : null,
            AfterUpdate = rollbackPhase == 2 ? () => RollbackAsync(claim.SiteId, request.NowUtc) : null,
        };
        var executor = new DeviceWriteBrokerExecutionUseCase(new DapperDeviceWriteBrokerAttemptStore(_dataSource!),
            new DapperDeviceWriteBrokerPlanResolver(_dataSource!), driver, new FixedClock(request.NowUtc));
        var result = await executor.ExecuteAsync(request, CancellationToken.None);
        Assert.True(result.ObservationRecorded);
        Assert.Equal(rollbackPhase switch
        {
            0 => DeviceWriteBrokerAttemptState.Verified,
            1 => DeviceWriteBrokerAttemptState.NotSent,
            _ => DeviceWriteBrokerAttemptState.Unknown,
        }, result.State);
        Assert.Equal(rollbackPhase == 1 ? 0 : 1, vendor.UpdateCount);
        Assert.False((await executor.ExecuteAsync(request, CancellationToken.None)).DriverInvoked);
        Assert.False((await executor.ExecuteAsync(request with { AttemptId = Guid.NewGuid() }, CancellationToken.None)).Admitted);
        if (rollbackPhase == 2)
        {
            await AssertRollbackFallbackBlockedAsync(executor, request, pilot.Request.OutboxItemId);
        }
        await using var audit = _dataSource!.CreateCommand("SELECT state FROM device_write_broker_attempts WHERE attempt_id = $1;");
        audit.Parameters.AddWithValue(request.AttemptId);
        Assert.Equal(result.State.ToString(), await audit.ExecuteScalarAsync());
        Assert.Equal(rollbackPhase == 1 ? 0 : 1, vendor.UpdateCount);
    }

    [Fact]
    public async Task Broker_resolver_requires_exact_live_product_claim_and_never_promotes_legacy_shadow_snapshot()
    {
        var pilot = await PrepareReleasedAsync();
        var claimed = await new DapperActivationPrewriteClaimStore(_dataSource!).TryClaimAsync(pilot.Request, CancellationToken.None);
        Assert.True(claimed.Claimed);
        var claim = claimed.Claim!;
        var request = new DeviceWriteBrokerBeginRequest(Guid.NewGuid(), claim.SiteId,
            new DateOnly(2026, 9, 23), claim.WindowId, claim.PayloadHash,
            ActivationWriterAuthority.ProductAgent, claim.WriterOwnerId, claim.SafetyRevision,
            claim.FencingToken, claim.ClaimId, pilot.Request.NowUtc);
        var resolver = new DapperDeviceWriteBrokerPlanResolver(_dataSource!);
        Assert.NotNull(await resolver.ResolveAsync(request, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(request with { ActivationClaimId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(request with { FencingToken = request.FencingToken + 1 }, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(request with { WriterOwnerId = "untrusted-writer" }, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(request with { WindowId = "Z2" }, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(request with { NowUtc = claim.ExpiresAtUtc }, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(request with
        {
            Authority = ActivationWriterAuthority.LegacyRunner, ActivationClaimId = null,
        }, CancellationToken.None));
    }

    [Fact]
    public async Task Broker_resolver_rejects_corrupted_claimed_payload_before_fake_vendor_update()
    {
        var pilot = await PrepareReleasedAsync();
        var claimed = await new DapperActivationPrewriteClaimStore(_dataSource!).TryClaimAsync(pilot.Request, CancellationToken.None);
        Assert.True(claimed.Claimed);
        var claim = claimed.Claim!;
        var request = new DeviceWriteBrokerBeginRequest(Guid.NewGuid(), claim.SiteId,
            new DateOnly(2026, 9, 23), claim.WindowId, claim.PayloadHash,
            ActivationWriterAuthority.ProductAgent, claim.WriterOwnerId, claim.SafetyRevision,
            claim.FencingToken, claim.ClaimId, pilot.Request.NowUtc);
        await using (var corrupt = _dataSource!.CreateCommand("UPDATE activation_outbox SET payload_json = '{\"siteId\":\"corrupt\"}'::jsonb WHERE claim_id = $1;"))
        {
            corrupt.Parameters.AddWithValue(claim.ClaimId);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }
        using var vendor = new DeviceWriteBrokerAttemptIntegrationTests.FakeVendor();
        using var client = new HttpClient(vendor);
        var executor = new DeviceWriteBrokerExecutionUseCase(new DapperDeviceWriteBrokerAttemptStore(_dataSource!),
            new DapperDeviceWriteBrokerPlanResolver(_dataSource!),
            new DeviceWriteBrokerAttemptIntegrationTests.FakeDriver(client), new FixedClock(request.NowUtc));
        var result = await executor.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(DeviceWriteBrokerAttemptState.NotSent, result.State);
        Assert.True(result.ObservationRecorded);
        Assert.Equal(0, vendor.UpdateCount);
    }

    private async Task RollbackAsync(string siteId, DateTimeOffset now)
    {
        var rollback = await new DapperActivationCutoverStore(_dataSource!).RollbackAsync(
            new ActivationRollbackRequest(Guid.NewGuid(), siteId, 2, "operator-d",
                "test rollback during broker execution", now), CancellationToken.None);
        Assert.True(rollback.RolledBack);
    }

    private async Task AssertRollbackFallbackBlockedAsync(DeviceWriteBrokerExecutionUseCase executor,
        DeviceWriteBrokerBeginRequest request, Guid outboxItemId)
    {
        // Rollback cancels the outbox, but cannot release the independent
        // broker latch even if a caller asks to fall back to legacy authority.
        var fallback = await executor.ExecuteAsync(request with
        {
            AttemptId = Guid.NewGuid(), Authority = ActivationWriterAuthority.LegacyRunner, ActivationClaimId = null,
        }, CancellationToken.None);
        Assert.False(fallback.Admitted);
        Assert.Equal("device-write-broker-unresolved-attempt", fallback.OutcomeCode);
        await using var cancelled = _dataSource!.CreateCommand("SELECT status, claim_id FROM activation_outbox WHERE outbox_item_id = $1;");
        cancelled.Parameters.AddWithValue(outboxItemId);
        await using var reader = await cancelled.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Cancelled", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
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
