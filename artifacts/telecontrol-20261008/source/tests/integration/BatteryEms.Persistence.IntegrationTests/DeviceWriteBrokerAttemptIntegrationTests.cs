using BatteryEms.Adapters.Persistence;
using BatteryEms.Adapters.DeyeCloud;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BatteryEms.Persistence.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public sealed class DeviceWriteBrokerAttemptIntegrationTests : IAsyncLifetime
{
    private static readonly string[] PlanStartTimes = ["00:00", "04:00", "08:00", "12:00", "16:00", "20:00"];
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 20, 55, 25, TimeSpan.Zero);
    private NpgsqlDataSource _source = null!;
    private string _connection = string.Empty;
    private DeviceWriteBrokerBeginRequest _request = null!;

    public async Task InitializeAsync()
    {
        var options = PersistenceOptions.FromHostPort(
            Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "127.0.0.1",
            int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var port) ? port : 5432,
            Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "bessems",
            Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "bessems",
            Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "bessems");
        _connection = options.ConnectionString;
        _source = NpgsqlDataSource.Create(_connection);
        await new BessDbMigrator(_source, _connection, NullLogger<BessDbMigrator>.Instance).MigrateAsync(CancellationToken.None);
        await using (var reset = _source.CreateCommand("TRUNCATE device_write_broker_attempts, device_write_broker_sites, activation_writer_safety, activation_writer_leases, activation_writer_fence_sequences;"))
        {
            await reset.ExecuteNonQueryAsync();
        }
        var safety = new DapperActivationWriterSafetyStore(_source);
        var state = ActivationSafetyState.InitialFailClosed("broker-site", Now, "operator-a", "test legacy broker authority") with
        {
            KillSwitchEngaged = false,
        };
        Assert.True(await safety.CompareExchangeStateAsync(state, null, CancellationToken.None));
        var lease = await safety.TryAcquireLeaseAsync("broker-site", "legacy-process-a", Now, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(lease.Acquired);
        _request = new DeviceWriteBrokerBeginRequest(Guid.NewGuid(), "broker-site", new DateOnly(2026, 9, 23),
            "Z1", new string('A', 64), ActivationWriterAuthority.LegacyRunner,
            "legacy-process-a", 1, lease.Lease!.FencingToken, null, Now);
    }

    public async Task DisposeAsync() => await _source.DisposeAsync();

    [Fact]
    public async Task Concurrent_broker_instances_admit_only_one_unresolved_attempt_and_replay_is_not_a_new_permit()
    {
        var requests = Enumerable.Range(0, 12).Select(_ => _request with { AttemptId = Guid.NewGuid() }).ToArray();
        var results = await Task.WhenAll(requests.Select(request => new DapperDeviceWriteBrokerAttemptStore(_source).BeginAsync(request, CancellationToken.None)));
        var winner = Assert.Single(results, result => result.Accepted);
        Assert.False(winner.IsReplay);
        Assert.Equal(11, results.Count(result => result.BlockingCode == "device-write-broker-unresolved-attempt"));
        var winningRequest = requests[Array.IndexOf(results, winner)];
        var store = new DapperDeviceWriteBrokerAttemptStore(_source);
        var replay = await store.BeginAsync(winningRequest, CancellationToken.None);
        Assert.True(replay.Accepted);
        Assert.True(replay.IsReplay);
        var conflict = await store.BeginAsync(winningRequest with { PayloadHash = new string('B', 64) }, CancellationToken.None);
        Assert.False(conflict.Accepted);
        Assert.Equal("device-write-broker-attempt-conflict", conflict.BlockingCode);
        await using var count = _source.CreateCommand("SELECT COUNT(*) FROM device_write_broker_attempts;");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Prepared_attempt_survives_database_client_loss_lease_takeover_and_process_restart()
    {
        var store = new DapperDeviceWriteBrokerAttemptStore(_source);
        Assert.True((await store.BeginAsync(_request, CancellationToken.None)).Accepted);
        await _source.DisposeAsync();
        _source = NpgsqlDataSource.Create(_connection);
        var later = Now.AddSeconds(61);
        var takeover = await new DapperActivationWriterSafetyStore(_source).TryAcquireLeaseAsync(
            _request.SiteId, "replacement-process", later, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(takeover.Acquired);
        Assert.True(takeover.Lease!.FencingToken > _request.FencingToken);
        var replacement = _request with
        {
            AttemptId = Guid.NewGuid(), WriterOwnerId = "replacement-process",
            FencingToken = takeover.Lease.FencingToken, NowUtc = later,
        };
        var restartedStore = new DapperDeviceWriteBrokerAttemptStore(_source);
        var rejected = await restartedStore.BeginAsync(replacement, CancellationToken.None);
        Assert.False(rejected.Accepted);
        Assert.Equal("device-write-broker-unresolved-attempt", rejected.BlockingCode);
        var lateStart = await restartedStore.MarkInitiatedAsync(_request.AttemptId, _request.WriterOwnerId, later, CancellationToken.None);
        Assert.False(lateStart.Accepted);
        Assert.Equal("device-write-broker-lease-invalid", lateStart.BlockingCode);
        var unknown = await restartedStore.ObserveAsync(_request.AttemptId, _request.WriterOwnerId,
            DeviceWriteBrokerObservation.Unknown, later, CancellationToken.None);
        Assert.True(unknown.Accepted);
        var automaticClear = await restartedStore.ObserveAsync(_request.AttemptId, _request.WriterOwnerId,
            DeviceWriteBrokerObservation.Verified, later.AddSeconds(1), CancellationToken.None);
        Assert.False(automaticClear.Accepted);
        Assert.Equal("device-write-broker-reconciliation-required", automaticClear.BlockingCode);
        Assert.False((await restartedStore.BeginAsync(replacement, CancellationToken.None)).Accepted);
    }

    [Fact]
    public async Task Kill_switch_between_prepare_and_initiate_denies_network_start()
    {
        var store = new DapperDeviceWriteBrokerAttemptStore(_source);
        Assert.True((await store.BeginAsync(_request, CancellationToken.None)).Accepted);
        var safety = new DapperActivationWriterSafetyStore(_source);
        var current = (await safety.FindStateAsync(_request.SiteId, CancellationToken.None))!;
        Assert.True(await safety.CompareExchangeStateAsync(current with
        {
            KillSwitchEngaged = true, Revision = 2, UpdatedAtUtc = Now.AddSeconds(1), Reason = "test emergency stop",
        }, 1, CancellationToken.None));
        var started = await store.MarkInitiatedAsync(_request.AttemptId, _request.WriterOwnerId, Now.AddSeconds(2), CancellationToken.None);
        Assert.False(started.Accepted);
        Assert.Equal("activation-kill-switch-engaged", started.BlockingCode);
        var notSent = await store.ObserveAsync(_request.AttemptId, _request.WriterOwnerId,
            DeviceWriteBrokerObservation.NotSent, Now.AddSeconds(2), CancellationToken.None);
        Assert.True(notSent.Accepted);
        var next = await store.BeginAsync(_request with { AttemptId = Guid.NewGuid(), NowUtc = Now.AddSeconds(3) }, CancellationToken.None);
        Assert.False(next.Accepted);
        Assert.Equal("activation-kill-switch-engaged", next.BlockingCode);
    }

    [Fact]
    public async Task Initiated_attempt_cannot_be_called_not_sent_and_verified_window_cannot_run_twice()
    {
        var store = new DapperDeviceWriteBrokerAttemptStore(_source);
        Assert.True((await store.BeginAsync(_request, CancellationToken.None)).Accepted);
        Assert.False((await store.ObserveAsync(_request.AttemptId, _request.WriterOwnerId,
            DeviceWriteBrokerObservation.Verified, Now, CancellationToken.None)).Accepted);
        var backdated = await store.MarkInitiatedAsync(_request.AttemptId, _request.WriterOwnerId, Now.AddSeconds(-1), CancellationToken.None);
        Assert.False(backdated.Accepted);
        Assert.Equal("device-write-broker-clock-before-attempt", backdated.BlockingCode);
        var started = await store.MarkInitiatedAsync(_request.AttemptId, _request.WriterOwnerId, Now, CancellationToken.None);
        Assert.True(started.Accepted);
        Assert.False(started.IsReplay);
        var startReplay = await store.MarkInitiatedAsync(_request.AttemptId, _request.WriterOwnerId, Now, CancellationToken.None);
        Assert.True(startReplay.IsReplay);
        var notSent = await store.ObserveAsync(_request.AttemptId, _request.WriterOwnerId,
            DeviceWriteBrokerObservation.NotSent, Now.AddSeconds(1), CancellationToken.None);
        Assert.False(notSent.Accepted);
        Assert.Equal("device-write-broker-mutation-may-have-started", notSent.BlockingCode);
        var verified = await store.ObserveAsync(_request.AttemptId, _request.WriterOwnerId,
            DeviceWriteBrokerObservation.Verified, Now.AddSeconds(45), CancellationToken.None);
        Assert.True(verified.Accepted);
        var again = await store.BeginAsync(_request with { AttemptId = Guid.NewGuid(), NowUtc = Now.AddSeconds(46) }, CancellationToken.None);
        Assert.False(again.Accepted);
        Assert.Equal("device-write-broker-authorization-consumed", again.BlockingCode);
    }

    [Fact]
    public async Task Prewrite_not_sent_attempts_are_bounded_to_three_and_sql_prevents_a_second_open_attempt()
    {
        var store = new DapperDeviceWriteBrokerAttemptStore(_source);
        for (var index = 0; index < 3; index++)
        {
            var request = _request with { AttemptId = Guid.NewGuid(), NowUtc = Now.AddSeconds(index * 2) };
            Assert.True((await store.BeginAsync(request, CancellationToken.None)).Accepted);
            Assert.True((await store.ObserveAsync(request.AttemptId, request.WriterOwnerId,
                DeviceWriteBrokerObservation.NotSent, request.NowUtc.AddSeconds(1), CancellationToken.None)).Accepted);
        }
        var fourth = await store.BeginAsync(_request with { AttemptId = Guid.NewGuid(), NowUtc = Now.AddSeconds(6) }, CancellationToken.None);
        Assert.False(fourth.Accepted);
        Assert.Equal("device-write-broker-prewrite-attempt-limit", fourth.BlockingCode);

        // Force two closed rows back to Prepared to verify the DB invariant itself.
        await using (var first = _source.CreateCommand("""
            UPDATE device_write_broker_attempts SET state = 'Prepared', observed_at_utc = NULL, outcome_code = NULL
            WHERE attempt_id = (SELECT attempt_id FROM device_write_broker_attempts ORDER BY begun_at_utc LIMIT 1);
            """)) { await first.ExecuteNonQueryAsync(); }
        await using var second = _source.CreateCommand("""
            UPDATE device_write_broker_attempts SET state = 'Prepared', observed_at_utc = NULL, outcome_code = NULL
            WHERE attempt_id = (SELECT attempt_id FROM device_write_broker_attempts WHERE state = 'NotSent' ORDER BY begun_at_utc LIMIT 1);
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => second.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("ux_device_write_broker_unresolved_site", error.ConstraintName);
    }

    [Fact]
    public async Task Executor_concurrent_requests_make_one_fake_vendor_update_and_one_fresh_readback()
    {
        var plan = Plan();
        var request = PlanRequest(plan);
        using var vendor = new FakeVendor();
        using var client = new HttpClient(vendor);
        var driver = new FakeDriver(client);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Executor(new DapperDeviceWriteBrokerAttemptStore(_source), plan, driver)
                .ExecuteAsync(request, CancellationToken.None)));
        Assert.Equal(1, vendor.UpdateCount);
        Assert.Equal(1, vendor.ReadCount);
        Assert.Single(results, result => result.DriverInvoked);
        Assert.Single(results, result => result.ObservationRecorded && result.State == DeviceWriteBrokerAttemptState.Verified);
        Assert.Equal("Verified", await AttemptStateAsync(request.AttemptId));
        var replacement = await Executor(new DapperDeviceWriteBrokerAttemptStore(_source), plan, driver)
            .ExecuteAsync(request with { AttemptId = Guid.NewGuid() }, CancellationToken.None);
        Assert.False(replacement.Admitted);
        Assert.Equal(1, vendor.UpdateCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executor_unknown_or_lost_update_response_remains_latched_after_client_restart(bool loseResponse)
    {
        var plan = Plan();
        var request = PlanRequest(plan);
        using var vendor = new FakeVendor { LoseUpdateResponse = loseResponse, MatchedReadback = false };
        using var client = new HttpClient(vendor);
        var driver = new FakeDriver(client);
        var result = await Executor(new DapperDeviceWriteBrokerAttemptStore(_source), plan, driver)
            .ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(DeviceWriteBrokerAttemptState.Unknown, result.State);
        Assert.True(result.ObservationRecorded);
        Assert.Equal(1, vendor.UpdateCount);
        Assert.Equal(loseResponse ? 0 : 1, vendor.ReadCount);
        await _source.DisposeAsync();
        _source = NpgsqlDataSource.Create(_connection);
        var restarted = Executor(new DapperDeviceWriteBrokerAttemptStore(_source), plan, driver);
        Assert.False((await restarted.ExecuteAsync(request, CancellationToken.None)).DriverInvoked);
        Assert.False((await restarted.ExecuteAsync(request with { AttemptId = Guid.NewGuid() }, CancellationToken.None)).Admitted);
        Assert.Equal("Unknown", await AttemptStateAsync(request.AttemptId));
        Assert.Equal(1, vendor.UpdateCount);
    }

    [Fact]
    public async Task Lost_initiation_commit_acknowledgement_never_calls_vendor_or_clears_latch()
    {
        var plan = Plan();
        var request = PlanRequest(plan);
        var durable = new DapperDeviceWriteBrokerAttemptStore(_source);
        using var vendor = new FakeVendor();
        using var client = new HttpClient(vendor);
        var driver = new FakeDriver(client);
        var ambiguous = new FaultStore(durable) { LoseInitiationAcknowledgement = true };
        await Assert.ThrowsAsync<IOException>(() => Executor(ambiguous, plan, driver).ExecuteAsync(request, CancellationToken.None));
        Assert.Equal("Initiated", await AttemptStateAsync(request.AttemptId));
        Assert.Equal(0, vendor.UpdateCount);
        Assert.False((await Executor(durable, plan, driver).ExecuteAsync(request, CancellationToken.None)).DriverInvoked);
        Assert.False((await Executor(durable, plan, driver).ExecuteAsync(request with { AttemptId = Guid.NewGuid() }, CancellationToken.None)).Admitted);
        Assert.Equal(0, vendor.UpdateCount);
    }

    [Fact]
    public async Task Lost_observation_after_send_leaves_initiated_not_retryable()
    {
        var plan = Plan();
        var request = PlanRequest(plan);
        var durable = new DapperDeviceWriteBrokerAttemptStore(_source);
        using var vendor = new FakeVendor();
        using var client = new HttpClient(vendor);
        var driver = new FakeDriver(client);
        await Assert.ThrowsAsync<IOException>(() => Executor(new FaultStore(durable) { FailObservation = true }, plan, driver)
            .ExecuteAsync(request, CancellationToken.None));
        Assert.Equal("Initiated", await AttemptStateAsync(request.AttemptId));
        Assert.Equal(1, vendor.UpdateCount);
        Assert.False((await Executor(durable, plan, driver).ExecuteAsync(request, CancellationToken.None)).DriverInvoked);
        Assert.False((await Executor(durable, plan, driver).ExecuteAsync(request with { AttemptId = Guid.NewGuid() }, CancellationToken.None)).Admitted);
        Assert.Equal(1, vendor.UpdateCount);
    }

    [Fact]
    public async Task Cancellation_after_initiation_commit_leaves_latch_and_no_network_call()
    {
        var plan = Plan();
        var request = PlanRequest(plan);
        using var cancellation = new CancellationTokenSource();
        using var vendor = new FakeVendor();
        using var client = new HttpClient(vendor);
        var driver = new FakeDriver(client);
        var durable = new DapperDeviceWriteBrokerAttemptStore(_source);
        var store = new FaultStore(durable) { AfterInitiation = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(store, plan, driver).ExecuteAsync(request, cancellation.Token));
        Assert.Equal("Initiated", await AttemptStateAsync(request.AttemptId));
        Assert.False((await Executor(durable, plan, driver).ExecuteAsync(request, CancellationToken.None)).DriverInvoked);
        Assert.Equal(0, vendor.UpdateCount);
    }

    [Fact]
    public async Task Kill_switch_during_read_only_preflight_blocks_update_and_records_not_sent()
    {
        var plan = Plan();
        var request = PlanRequest(plan);
        using var vendor = new FakeVendor();
        using var client = new HttpClient(vendor);
        var driver = new FakeDriver(client)
        {
            DuringPreflight = async () =>
            {
                var safety = new DapperActivationWriterSafetyStore(_source);
                var current = (await safety.FindStateAsync(request.SiteId, CancellationToken.None))!;
                Assert.True(await safety.CompareExchangeStateAsync(current with
                {
                    KillSwitchEngaged = true, Revision = 2, UpdatedAtUtc = Now, Reason = "test stop during preflight",
                }, 1, CancellationToken.None));
            },
        };
        var result = await Executor(new DapperDeviceWriteBrokerAttemptStore(_source), plan, driver).ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(DeviceWriteBrokerAttemptState.NotSent, result.State);
        Assert.False(result.DriverInvoked);
        Assert.Equal(0, vendor.UpdateCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Untrusted_plan_or_fail_closed_driver_never_reaches_mutation(bool defaultDriver)
    {
        var plan = Plan();
        var request = PlanRequest(plan);
        using var vendor = new FakeVendor();
        using var client = new HttpClient(vendor);
        IDeviceWriteBrokerDriver driver = defaultDriver ? new FailClosedDeviceWriteBrokerDriver() : new FakeDriver(client);
        var resolved = defaultDriver ? plan : plan with { SiteId = "different-site" };
        var result = await Executor(new DapperDeviceWriteBrokerAttemptStore(_source), resolved, driver)
            .ExecuteAsync(request with { NowUtc = Now.AddDays(10) }, CancellationToken.None);
        Assert.True(result.Admitted); // Client time is ignored in favour of the server clock.
        Assert.Equal(DeviceWriteBrokerAttemptState.NotSent, result.State);
        Assert.Equal(0, vendor.UpdateCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concrete_deye_driver_with_durable_executor_never_repeats_update_after_restart(bool loseResponse)
    {
        var plan = Plan();
        var request = PlanRequest(plan);
        using var vendor = new DeyeProtocolVendor { LoseUpdateResponse = loseResponse };
        using var client = new HttpClient(vendor) { BaseAddress = new Uri("https://fake-deye.invalid/") };
        var driver = new DeyeCloudDeviceWriteBrokerDriver(client,
            new DeyeBrokerDeviceOptions(request.SiteId, "test-station", "test-master", "test-slave", true), new Clock(),
            new DapperDeviceWriteBrokerAttemptStore(_source));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Executor(new DapperDeviceWriteBrokerAttemptStore(_source), plan, driver).ExecuteAsync(request, CancellationToken.None)));
        Assert.Equal(1, vendor.Updates);
        Assert.Single(results, result => result.DriverInvoked);
        Assert.Equal(loseResponse ? "Unknown" : "Verified", await AttemptStateAsync(request.AttemptId));
        Assert.Equal(loseResponse ? 0 : 1, vendor.Reads);
        await _source.DisposeAsync();
        _source = NpgsqlDataSource.Create(_connection);
        var restarted = Executor(new DapperDeviceWriteBrokerAttemptStore(_source), plan, driver);
        Assert.False((await restarted.ExecuteAsync(request, CancellationToken.None)).DriverInvoked);
        Assert.False((await restarted.ExecuteAsync(request with { AttemptId = Guid.NewGuid() }, CancellationToken.None)).Admitted);
        Assert.Equal(1, vendor.Updates);
    }

    [Fact]
    public async Task Kill_switch_during_concrete_driver_preflight_prevents_physical_update()
    {
        var plan = Plan();
        var request = PlanRequest(plan);
        using var vendor = new DeyeProtocolVendor
        {
            BeforeSecondSystemRead = async () =>
            {
                var safety = new DapperActivationWriterSafetyStore(_source);
                var current = (await safety.FindStateAsync(request.SiteId, CancellationToken.None))!;
                Assert.True(await safety.CompareExchangeStateAsync(current with
                {
                    Revision = 2, KillSwitchEngaged = true, UpdatedAtUtc = Now, Reason = "test stop at network boundary",
                }, 1, CancellationToken.None));
            },
        };
        using var client = new HttpClient(vendor) { BaseAddress = new Uri("https://fake-deye.invalid/") };
        var store = new DapperDeviceWriteBrokerAttemptStore(_source);
        var driver = new DeyeCloudDeviceWriteBrokerDriver(client,
            new DeyeBrokerDeviceOptions(request.SiteId, "test-station", "test-master", "test-slave", true), new Clock(), store);
        var result = await Executor(store, plan, driver).ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(DeviceWriteBrokerAttemptState.Unknown, result.State);
        Assert.Equal(0, vendor.Updates);
        Assert.Equal("Unknown", await AttemptStateAsync(request.AttemptId));
        Assert.False(await store.CanSendAsync(request, CancellationToken.None));
    }

    private DeviceWriteBrokerBeginRequest PlanRequest(ShadowPlanSnapshot plan) => _request with
    {
        PayloadHash = ActivationPayloadIntegrity.ComputeHash(plan),
    };

    [Fact]
    public async Task Preflight_that_runs_past_start_deadline_is_not_sent()
    {
        var plan = Plan();
        var request = PlanRequest(plan);
        var clock = new Clock();
        using var vendor = new FakeVendor();
        using var client = new HttpClient(vendor);
        var driver = new FakeDriver(client)
        {
            DuringPreflight = () => { clock.UtcNow = Now.AddMinutes(15); return Task.CompletedTask; },
        };
        var executor = new DeviceWriteBrokerExecutionUseCase(new DapperDeviceWriteBrokerAttemptStore(_source),
            new Resolver(plan), driver, clock);
        var result = await executor.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(DeviceWriteBrokerAttemptState.NotSent, result.State);
        Assert.Equal(0, vendor.UpdateCount);
    }

    [Fact]
    public async Task Resolver_mutating_its_original_lists_cannot_change_the_approved_payload_in_flight()
    {
        var plan = Plan();
        var request = PlanRequest(plan);
        using var vendor = new FakeVendor();
        using var client = new HttpClient(vendor);
        var driver = new FakeDriver(client)
        {
            DuringPreflight = () =>
            {
                var original = (ShadowTouInterval[])plan.Windows[0].Intervals;
                original[0] = original[0] with { PowerWatts = 70000 };
                return Task.CompletedTask;
            },
        };
        var result = await Executor(new DapperDeviceWriteBrokerAttemptStore(_source), plan, driver).ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(DeviceWriteBrokerAttemptState.Verified, result.State);
        Assert.Equal(1, vendor.UpdateCount);
        Assert.NotEqual(request.PayloadHash, ActivationPayloadIntegrity.ComputeHash(plan));
    }

    [Fact]
    public async Task Delay_in_initiation_commit_past_deadline_does_not_call_driver_or_clear_latch()
    {
        var plan = Plan();
        var request = PlanRequest(plan);
        var clock = new Clock();
        using var vendor = new FakeVendor();
        using var client = new HttpClient(vendor);
        var store = new FaultStore(new DapperDeviceWriteBrokerAttemptStore(_source))
        {
            AfterInitiation = () => clock.UtcNow = Now.AddMinutes(15),
        };
        var result = await new DeviceWriteBrokerExecutionUseCase(store, new Resolver(plan), new FakeDriver(client), clock)
            .ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(DeviceWriteBrokerAttemptState.Unknown, result.State);
        Assert.True(result.ObservationRecorded);
        Assert.False(result.DriverInvoked);
        Assert.Equal(0, vendor.UpdateCount);
        Assert.Equal("Unknown", await AttemptStateAsync(request.AttemptId));
    }

    private static DeviceWriteBrokerExecutionUseCase Executor(IDeviceWriteBrokerAttemptStore store,
        ShadowPlanSnapshot plan, IDeviceWriteBrokerDriver driver) => new(store, new Resolver(plan), driver, new Clock());

    private async Task<string?> AttemptStateAsync(Guid attemptId)
    {
        await using var command = _source.CreateCommand("SELECT state FROM device_write_broker_attempts WHERE attempt_id = $1;");
        command.Parameters.AddWithValue(attemptId);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static ShadowPlanSnapshot Plan() => new("broker-site", new DateOnly(2026, 9, 23), true, [],
        Enumerable.Range(1, 4).Select(index => new ShadowTouWindow($"Z{index}",
            PlanStartTimes
                .Select(start => new ShadowTouInterval(start, true, false, false, 10000, 30, 0)).ToArray())).ToArray());

    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = Now; }

    internal sealed class Resolver(ShadowPlanSnapshot plan) : IDeviceWriteBrokerPlanResolver
    {
        public Task<ShadowPlanSnapshot?> ResolveAsync(DeviceWriteBrokerBeginRequest request, CancellationToken cancellationToken)
            => Task.FromResult<ShadowPlanSnapshot?>(plan);
    }

    // Real HttpClient calls reach only this in-memory handler. No vendor address,
    // credential, network or physical device is contacted by these tests.
    internal sealed class FakeVendor : HttpMessageHandler
    {
        private int _updates;
        private int _reads;
        public int UpdateCount => Volatile.Read(ref _updates);
        public int ReadCount => Volatile.Read(ref _reads);
        public bool LoseUpdateResponse { get; init; }
        public bool MatchedReadback { get; init; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/order/sys/tou/update")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Interlocked.Increment(ref _updates);
                if (LoseUpdateResponse) { throw new HttpRequestException("Simulated response loss after vendor accepted update."); }
            }
            else
            {
                Assert.Equal("/fresh-readback", request.RequestUri.AbsolutePath);
                Interlocked.Increment(ref _reads);
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(MatchedReadback ? "matched" : "mismatch"),
            });
        }
    }

    internal sealed class FakeDriver(HttpClient client) : IDeviceWriteBrokerDriver
    {
        public Func<Task>? DuringPreflight { get; init; }
        public Func<Task>? AfterUpdate { get; init; }
        public async Task<bool> PreflightAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
        {
            if (DuringPreflight is not null) { await DuringPreflight(); }
            return true;
        }

        public async Task<DeviceWriteBrokerReadback> WriteOnceAndVerifyAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
        {
            using var content = new StringContent(ActivationPayloadIntegrity.ComputeWindowHash(envelope.Window));
            using var update = await client.PostAsync(new Uri("https://fake-broker-vendor.invalid/order/sys/tou/update"), content, cancellationToken);
            update.EnsureSuccessStatusCode();
            if (AfterUpdate is not null) { await AfterUpdate(); }
            using var read = await client.GetAsync(new Uri("https://fake-broker-vendor.invalid/fresh-readback"), cancellationToken);
            return await read.Content.ReadAsStringAsync(cancellationToken) == "matched"
                ? DeviceWriteBrokerReadback.Matched : DeviceWriteBrokerReadback.Unknown;
        }
    }

    private sealed class DeyeProtocolVendor : HttpMessageHandler
    {
        public bool LoseUpdateResponse { get; init; }
        public Func<Task>? BeforeSecondSystemRead { get; init; }
        private int _systemReads;
        public int Updates { get; private set; }
        public int Reads { get; private set; }
        private JsonArray? _wire;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            JsonObject result;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/station/device":
                    result = new JsonObject { ["deviceListItems"] = new JsonArray(Device("test-master"), Device("test-slave")) };
                    break;
                case "/device/latest":
                    result = new JsonObject { ["deviceDataList"] = new JsonArray(Telemetry("test-master"), Telemetry("test-slave")) };
                    break;
                case "/config/tou":
                    result = new JsonObject { ["timeUseSettingItems"] = new JsonArray(Enumerable.Range(0, 6)
                        .Select(_ => (JsonNode)new JsonObject()).ToArray()) };
                    break;
                case "/config/system":
                    _systemReads++;
                    if (_systemReads == 2 && BeforeSecondSystemRead is not null) { await BeforeSecondSystemRead(); }
                    result = new JsonObject(); break;
                case "/order/sys/tou/update":
                    Updates++;
                    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                    Assert.Equal("test-master", body["deviceSn"]!.ToString());
                    _wire = body["timeUseSettingItems"]!.DeepClone().AsArray();
                    if (LoseUpdateResponse) { throw new HttpRequestException("Simulated response loss after accepting update."); }
                    result = new JsonObject { ["orderId"] = 100 }; break;
                case "/order/100": result = new JsonObject { ["orderId"] = 100, ["status"] = 666, ["success"] = true }; break;
                case "/strategy/dynamicControl/read": Reads++; result = new JsonObject { ["orderId"] = 200 }; break;
                case "/strategy/dynamicControl/readResult":
                    result = new JsonObject { ["orderId"] = 200, ["touAction"] = "on", ["success"] = true,
                        ["timeUseSettingItems"] = _wire!.DeepClone() }; break;
                default: throw new InvalidOperationException("Unexpected broker vendor request.");
            }
            result["code"] = 1000000;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString()) };
        }

        private static JsonObject Device(string serial) => new() { ["deviceType"] = "INVERTER", ["deviceSn"] = serial, ["connectStatus"] = 1 };
        private static JsonObject Telemetry(string serial) => new()
        {
            ["deviceSn"] = serial, ["collectionTime"] = Now.ToUnixTimeSeconds(),
            ["dataList"] = new JsonArray(
                new JsonObject { ["key"] = "alarm", ["value"] = "0" },
                new JsonObject { ["key"] = "BMSSOC", ["value"] = "50" },
                new JsonObject { ["key"] = "BMSVoltage", ["value"] = "290" },
                new JsonObject { ["key"] = "batteryPower", ["value"] = "10000" }),
        };
    }

    private sealed class FaultStore(IDeviceWriteBrokerAttemptStore inner) : IDeviceWriteBrokerAttemptStore
    {
        public bool LoseInitiationAcknowledgement { get; init; }
        public bool FailObservation { get; init; }
        public Action? AfterInitiation { get; init; }
        public Task<DeviceWriteBrokerAttemptResult> BeginAsync(DeviceWriteBrokerBeginRequest request, CancellationToken cancellationToken)
            => inner.BeginAsync(request, cancellationToken);

        public async Task<DeviceWriteBrokerAttemptResult> MarkInitiatedAsync(Guid attemptId, string writerOwnerId,
            DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            var result = await inner.MarkInitiatedAsync(attemptId, writerOwnerId, nowUtc, cancellationToken);
            if (LoseInitiationAcknowledgement) { throw new IOException("Simulated committed transaction acknowledgement loss."); }
            AfterInitiation?.Invoke();
            return result;
        }

        public Task<DeviceWriteBrokerAttemptResult> ObserveAsync(Guid attemptId, string writerOwnerId,
            DeviceWriteBrokerObservation observation, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            if (FailObservation) { throw new IOException("Simulated database outage after update/readback."); }
            return inner.ObserveAsync(attemptId, writerOwnerId, observation, nowUtc, cancellationToken);
        }
    }
}
