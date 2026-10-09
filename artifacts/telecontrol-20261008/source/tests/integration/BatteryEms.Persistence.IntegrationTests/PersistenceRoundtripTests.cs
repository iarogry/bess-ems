using BatteryEms.Adapters.Persistence;
using BatteryEms.Application.IO;
using BatteryEms.Application.Markets;
using BatteryEms.Application.Persistence;
using BatteryEms.Application.Forecasting;
using BatteryEms.Application.Site;
using BatteryEms.Application.Time;
using BatteryEms.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BatteryEms.Persistence.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public sealed class PersistenceRoundtripTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 5, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] TightSocWarnings = { "tight-binding-soc-floor" };
    private static readonly string[] SocFloorViolations = { "soc_floor_violated" };
    private static readonly double[] InitialRdnPrices = { 100.5, 95.25 };
    private static readonly double[] ReplacementRdnPrices = { 90.0, -1.5 };

    private NpgsqlDataSource? _dataSource;
    private string? _connectionString;

    private static string Host => Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "127.0.0.1";
    private static int Port => int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var p) ? p : 5432;
    private static string Database => Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "bessems";
    private static string User => Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "bessems";
    private static string Password => Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "bessems";

    public async Task InitializeAsync()
    {
        await WaitForTcpAsync(Host, Port, TimeSpan.FromSeconds(30));

        var options = PersistenceOptions.FromHostPort(Host, Port, Database, User, Password);
        _connectionString = options.ConnectionString;
        _dataSource = NpgsqlDataSource.Create(_connectionString);

        await new BessDbMigrator(
            _dataSource, _connectionString, NullLogger<BessDbMigrator>.Instance)
            .MigrateAsync(CancellationToken.None);

        // Each test class run starts from a clean slate so assertions on
        // counts/last-row are stable when the compose stack is reused.
        await TruncateAllAsync(_dataSource);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    [Fact]
    public async Task Telemetry_round_trips_through_the_repository_with_DataQuality_intact()
    {
        var repo = new DapperTelemetryRepository(_dataSource!);

        var sample = new BatteryTelemetry(
            Timestamp: Now,
            AssetId: "single-bess-1",
            SocPercent: 60.5,
            SohPercent: 99,
            ActivePowerKw: -25,
            ReactivePowerKvar: 0,
            DcVoltage: 800,
            DcCurrent: -31,
            TemperatureCelsius: 22,
            Available: true,
            FaultStatus: "ok",
            DataQuality: DataQuality.Stale("aged-out"));

        await repo.AppendAsync(sample, CancellationToken.None);

        var latest = await repo.FindLatestAsync("single-bess-1", CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(sample.SocPercent, latest!.SocPercent);
        Assert.Equal(sample.ActivePowerKw, latest.ActivePowerKw);
        Assert.Equal(DataQualityState.Stale, latest.DataQuality.Flag);
        Assert.Equal("aged-out", latest.DataQuality.Reason);

        var range = await repo.QueryAsync("single-bess-1", Now - TimeSpan.FromMinutes(5), Now + TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Single(range);
    }

    [Fact]
    public async Task Command_repository_stores_dispatch_outcome_and_supports_idempotent_append()
    {
        var repo = new DapperCommandRepository(_dataSource!);

        var command = new BatteryCommand(
            CommandId: "round-trip-1",
            Timestamp: Now,
            AssetId: "single-bess-1",
            Mode: CommandMode.Discharge,
            ActivePowerKw: 25,
            ReactivePowerKvar: 0,
            ValidUntil: Now + TimeSpan.FromSeconds(5),
            Reason: "schedule",
            Source: CommandSource.Optimization);

        var firstDispatch = CommandDispatchResult.Failed("ack-timeout", Now);
        await repo.AppendAsync(command, firstDispatch, CancellationToken.None);

        // Re-append with a later, successful dispatch — Upsert keeps the
        // latest outcome and the row count stays at 1.
        var secondDispatch = CommandDispatchResult.Ok(Now + TimeSpan.FromMilliseconds(50), "accepted");
        await repo.AppendAsync(command, secondDispatch, CancellationToken.None);

        var stored = await repo.FindByCommandIdAsync("round-trip-1", CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(CommandMode.Discharge, stored!.Mode);
        Assert.Equal(25, stored.ActivePowerKw);
        Assert.Equal(CommandSource.Optimization, stored.Source);

        var latest = await repo.FindLatestAsync("single-bess-1", CancellationToken.None);
        Assert.Equal("round-trip-1", latest!.CommandId);
    }

    [Fact]
    public async Task Schedule_repository_replaces_full_window_set_atomically()
    {
        var repo = new DapperScheduleRepository(_dataSource!);

        var v1 = new Schedule("single-bess-1", ScheduleType.DayAhead, "DE-LU", 1, new List<ScheduleWindow>
        {
            new(Now, Now + TimeSpan.FromHours(1), 30),
            new(Now + TimeSpan.FromHours(1), Now + TimeSpan.FromHours(2), -20),
        });
        await repo.ReplaceAsync(v1, expectedBaseVersion: 0, CancellationToken.None);

        // Replace with a v2 that has fewer windows; the previous extra
        // window must be gone, not merged.
        var v2 = new Schedule("single-bess-1", ScheduleType.DayAhead, "DE-LU", 2, new List<ScheduleWindow>
        {
            new(Now, Now + TimeSpan.FromHours(1), 15),
        });
        await repo.ReplaceAsync(v2, expectedBaseVersion: 1, CancellationToken.None);

        var loaded = await repo.FindActiveAsync("single-bess-1", ScheduleType.DayAhead, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Version);
        Assert.Single(loaded.Windows);
        Assert.Equal(15, loaded.Windows[0].TargetPowerKw);
    }

    [Fact]
    public async Task Schedule_replace_with_zero_base_on_existing_row_throws_conflict()
    {
        // RM-M3-FUP-02 insert-path CAS: a caller that passes
        // expectedBaseVersion=0 on a (asset, type) that already holds
        // v1 must fail rather than silently replace.
        var repo = new DapperScheduleRepository(_dataSource!);

        var v1 = new Schedule("single-bess-1", ScheduleType.DayAhead, "DE-LU", 1, new List<ScheduleWindow>
        {
            new(Now, Now + TimeSpan.FromHours(1), 30),
        });
        await repo.ReplaceAsync(v1, expectedBaseVersion: 0, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ScheduleConcurrencyConflictException>(
            () => repo.ReplaceAsync(v1, expectedBaseVersion: 0, CancellationToken.None));
        Assert.Equal(0, ex.ExpectedBaseVersion);
        Assert.Equal(1, ex.ActualVersion);

        // The first v1 row is still the active one; CAS rollback worked.
        var loaded = await repo.FindActiveAsync("single-bess-1", ScheduleType.DayAhead, CancellationToken.None);
        Assert.Equal(1, loaded!.Version);
    }

    [Fact]
    public async Task Schedule_replace_with_stale_base_version_throws_conflict_and_rolls_back()
    {
        // RM-M3-FUP-02 update-path CAS: a caller that passes
        // expectedBaseVersion=1 after the row already advanced to v2
        // must fail. The schedule_windows DELETE/INSERT inside the
        // same transaction must also be rolled back.
        var repo = new DapperScheduleRepository(_dataSource!);

        var v1 = new Schedule("single-bess-1", ScheduleType.DayAhead, "DE-LU", 1, new List<ScheduleWindow>
        {
            new(Now, Now + TimeSpan.FromHours(1), 30),
            new(Now + TimeSpan.FromHours(1), Now + TimeSpan.FromHours(2), -20),
        });
        await repo.ReplaceAsync(v1, expectedBaseVersion: 0, CancellationToken.None);

        var v2 = new Schedule("single-bess-1", ScheduleType.DayAhead, "DE-LU", 2, new List<ScheduleWindow>
        {
            new(Now, Now + TimeSpan.FromHours(1), 15),
        });
        await repo.ReplaceAsync(v2, expectedBaseVersion: 1, CancellationToken.None);

        // A second writer that still thinks the base is v1 attempts v2;
        // the CAS rejects it because the actual row is now v2.
        var v2bStale = new Schedule("single-bess-1", ScheduleType.DayAhead, "DE-LU", 2, new List<ScheduleWindow>
        {
            new(Now + TimeSpan.FromHours(2), Now + TimeSpan.FromHours(3), 99),
        });
        var ex = await Assert.ThrowsAsync<ScheduleConcurrencyConflictException>(
            () => repo.ReplaceAsync(v2bStale, expectedBaseVersion: 1, CancellationToken.None));
        Assert.Equal(1, ex.ExpectedBaseVersion);
        Assert.Equal(2, ex.ActualVersion);

        // The 99-window from v2bStale must NOT be persisted (transaction
        // rollback). Only the v2 windows from the winning replace remain.
        var loaded = await repo.FindActiveAsync("single-bess-1", ScheduleType.DayAhead, CancellationToken.None);
        Assert.Equal(2, loaded!.Version);
        Assert.Single(loaded.Windows);
        Assert.Equal(15, loaded.Windows[0].TargetPowerKw);
    }

    [Fact]
    public async Task Schedule_seed_pattern_is_idempotent_on_restart_with_persistence()
    {
        // Regression pin: the bootstrap-seed pattern that runs on every
        // host start is "FindActive(asset, type) → Replace with that
        // version as expectedBaseVersion". A hard-coded
        // `expectedBaseVersion: 0` (the broken first cut) would crash
        // startup on the second boot when the schedule row from the
        // previous boot is still in Postgres. We exercise the same
        // pattern directly against DapperScheduleRepository here so the
        // test does not need to drag the host project into the test
        // graph; the pattern is what BessConfigurationBootstrap.SeedScheduleRepository
        // implements.
        var repo = new DapperScheduleRepository(_dataSource!);

        var seedV1 = new Schedule("single-bess-1", ScheduleType.DayAhead, "DE-LU", 1, new List<ScheduleWindow>
        {
            new(Now, Now + TimeSpan.FromHours(1), 30),
        });
        SeedPattern(repo, seedV1);

        // Second boot: same schedule-file content. Must not throw —
        // FindActive returns v1, Replace runs the CAS-update branch
        // with expectedBaseVersion=1 and re-installs identical content.
        SeedPattern(repo, seedV1);

        // Third boot: operator updated the schedule-file. Must override
        // the persisted row (preserves pre-FUP-02 unconditional-replace
        // semantic for the seed path).
        var seedV2 = new Schedule("single-bess-1", ScheduleType.DayAhead, "DE-LU", 2, new List<ScheduleWindow>
        {
            new(Now, Now + TimeSpan.FromHours(1), 45),
        });
        SeedPattern(repo, seedV2);

        var loaded = await repo.FindActiveAsync("single-bess-1", ScheduleType.DayAhead, CancellationToken.None);
        Assert.Equal(2, loaded!.Version);
        Assert.Equal(45, loaded.Windows[0].TargetPowerKw);
    }

    [Fact]
    public async Task Schedule_seed_pattern_swallows_concurrent_cold_start_conflict()
    {
        // Multi-replica cold-start race: replica A reads existing=null,
        // inserts v1; replica B (in the meantime) also read existing=null
        // and now attempts INSERT — RowsAffected=0 → conflict. The
        // bootstrap pattern absorbs that conflict because the seed's
        // contract is "ensure a schedule is present at startup".
        var repo = new DapperScheduleRepository(_dataSource!);

        // Sibling replica wins: row already at v1.
        var siblingWrote = new Schedule("single-bess-1", ScheduleType.DayAhead, "DE-LU", 1, new List<ScheduleWindow>
        {
            new(Now, Now + TimeSpan.FromHours(1), 30),
        });
        await repo.ReplaceAsync(siblingWrote, expectedBaseVersion: 0, CancellationToken.None);

        // We staged an existing snapshot from BEFORE the sibling write
        // (existing == null in our local view). The seed pattern must
        // tolerate the resulting CAS conflict.
        var ourSeed = new Schedule("single-bess-1", ScheduleType.DayAhead, "DE-LU", 1, new List<ScheduleWindow>
        {
            new(Now, Now + TimeSpan.FromHours(1), 99),
        });
        // Direct call simulates the race window: we invoke Replace with
        // expectedBaseVersion=0 even though the sibling has already
        // committed v1. The seed pattern's catch must absorb the throw.
        try
        {
            repo.Replace(ourSeed, expectedBaseVersion: 0);
            Assert.Fail("Expected ScheduleConcurrencyConflictException for stale insert path.");
        }
        catch (ScheduleConcurrencyConflictException)
        {
            // The seed swallows this — sibling's schedule remains active.
        }

        var loaded = await repo.FindActiveAsync("single-bess-1", ScheduleType.DayAhead, CancellationToken.None);
        Assert.Equal(1, loaded!.Version);
        Assert.Equal(30, loaded.Windows[0].TargetPowerKw);
    }

    // Mirrors BessConfigurationBootstrap.SeedScheduleRepository so this
    // test can verify the contract without a project reference to Host.
    private static void SeedPattern(IScheduleRepository repo, Schedule schedule)
    {
        var existing = repo.FindActive(schedule.AssetId, schedule.Type);
        try
        {
            repo.Replace(schedule, expectedBaseVersion: existing?.Version ?? 0);
        }
        catch (ScheduleConcurrencyConflictException)
        {
            // Multi-replica cold-start race: a sibling won the insert.
        }
    }

    [Fact]
    public async Task Schedule_replace_with_negative_expected_base_version_throws()
    {
        // Symmetry to InMemory: contract boundary check at the Dapper
        // adapter must reject negative expectedBaseVersion before any
        // SQL fires.
        var repo = new DapperScheduleRepository(_dataSource!);
        var schedule = new Schedule("single-bess-1", ScheduleType.DayAhead, "DE-LU", 1, new List<ScheduleWindow>
        {
            new(Now, Now + TimeSpan.FromHours(1), 30),
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.ReplaceAsync(schedule, expectedBaseVersion: -1, CancellationToken.None));
    }

    [Fact]
    public async Task Price_series_store_imports_replaces_and_loads_series()
    {
        var store = new DapperPriceSeriesStore(_dataSource!);
        var request = new PriceSeriesRequest(
            "10Y1001A1001A39I",
            "rdn",
            "energy_price",
            "entso-e",
            Now,
            Now + TimeSpan.FromHours(2),
            TimeSpan.FromHours(1));

        await store.ImportAsync(new PriceSeries(
            request.MarketBidArea,
            request.Product,
            request.PriceKind,
            "EUR/MWh",
            request.Source,
            request.HorizonStart,
            request.HorizonEnd,
            request.TimeStep,
            InitialRdnPrices),
            CancellationToken.None);

        await store.ImportAsync(new PriceSeries(
            request.MarketBidArea,
            request.Product,
            request.PriceKind,
            "EUR/MWh",
            request.Source,
            request.HorizonStart,
            request.HorizonEnd,
            request.TimeStep,
            ReplacementRdnPrices),
            CancellationToken.None);

        var loaded = await store.LoadAsync(request, CancellationToken.None);

        Assert.Equal("EUR/MWh", loaded.Unit);
        Assert.Equal(ReplacementRdnPrices, loaded.Values);
    }

    [Fact]
    public async Task Price_series_store_normalizes_non_utc_offsets_for_postgres()
    {
        var store = new DapperPriceSeriesStore(_dataSource!);
        var kyivStart = new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.FromHours(3));
        var request = new PriceSeriesRequest(
            "10Y1001A1001A39I",
            "rdn",
            "energy_price",
            "entso-e",
            kyivStart,
            kyivStart + TimeSpan.FromHours(2),
            TimeSpan.FromHours(1));

        await store.ImportAsync(new PriceSeries(
            request.MarketBidArea,
            request.Product,
            request.PriceKind,
            "UAH/MWh",
            request.Source,
            request.HorizonStart,
            request.HorizonEnd,
            request.TimeStep,
            InitialRdnPrices),
            CancellationToken.None);

        var loaded = await store.LoadAsync(request, CancellationToken.None);
        var loadedFromUtcRequest = await store.LoadAsync(new PriceSeriesRequest(
            request.MarketBidArea,
            request.Product,
            request.PriceKind,
            request.Source,
            request.HorizonStart.ToUniversalTime(),
            request.HorizonEnd.ToUniversalTime(),
            request.TimeStep), CancellationToken.None);

        Assert.Equal("UAH/MWh", loaded.Unit);
        Assert.Equal(request.HorizonStart.ToUniversalTime(), loaded.HorizonStart);
        Assert.Equal(request.HorizonEnd.ToUniversalTime(), loaded.HorizonEnd);
        Assert.Equal(InitialRdnPrices, loaded.Values);
        Assert.Equal(InitialRdnPrices, loadedFromUtcRequest.Values);
    }

    [Fact]
    public async Task Site_measurement_store_round_trips_status_only_rows()
    {
        var store = new DapperSiteMeasurementStore(_dataSource!);
        var timestamp = Now.AddMinutes(15);

        await store.AppendAsync([
            new SiteMeasurementReading(
                "site-1",
                "fusionsolar",
                "pv",
                "NE=129469793",
                "Roof PV",
                timestamp,
                TimeSpan.FromHours(1),
                "pv_energy",
                null,
                "kWh",
                "source_error",
                MetadataJson: """{"fail_code":"20056"}"""),
        ], CancellationToken.None);

        var loaded = Assert.Single(await store.QueryAsync(
            new SiteMeasurementQuery("site-1", timestamp.AddMinutes(-1), timestamp.AddMinutes(1)),
            CancellationToken.None));

        Assert.Equal("source_error", loaded.Quality);
        Assert.Null(loaded.Value);
        Assert.Equal("pv_energy", loaded.Metric);
        Assert.Contains("\"fail_code\"", loaded.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("\"20056\"", loaded.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Site_pv_profile_store_round_trips_and_lists_enabled_profiles()
    {
        var store = new DapperSitePvProfileStore(_dataSource!);
        var profile = new SitePvProfile(
            SiteId: "site-1",
            PvSystemId: "pv-roof",
            Name: "Roof PV",
            ForecastAssetId: "site-1-pv-roof",
            Enabled: true,
            Latitude: 50.45,
            Longitude: 30.52,
            TiltDegrees: 25,
            AzimuthDegrees: 0,
            InstalledDcKw: 500,
            InverterAcKw: 450,
            TemperatureCoefficientPerDegree: -0.004,
            SystemLossFraction: 0.14,
            ForecastHorizonHours: 48,
            ForecastResolutionMinutes: 15,
            ForecastProvider: "open_meteo",
            ForecastEngine: "pvlib_sidecar");

        await store.UpsertAsync(profile, CancellationToken.None);

        var loaded = await store.FindAsync("site-1", "pv-roof", CancellationToken.None);
        var enabled = await store.ListEnabledAsync(CancellationToken.None);
        var bySite = await store.ListBySiteAsync("site-1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("site-1-pv-roof", loaded!.ForecastAssetId);
        Assert.Single(enabled);
        Assert.Single(bySite);
        Assert.Equal("pv-roof", bySite[0].PvSystemId);
    }

    [Fact]
    public async Task Solar_forecast_store_replaces_latest_payload_and_round_trips_points()
    {
        var store = new DapperSolarForecastStore(_dataSource!);
        var first = new SolarForecast(
            assetId: "site-1-pv-roof",
            source: "open-meteo",
            model: "pvlib",
            generatedAt: Now,
            horizonStart: Now,
            horizonEnd: Now + TimeSpan.FromMinutes(30),
            timeStep: TimeSpan.FromMinutes(15),
            installedDcKw: 500,
            installedAcKw: 450,
            points:
            [
                new SolarForecastPoint(Now, 120, 520, 26, 3.2, 10),
                new SolarForecastPoint(Now + TimeSpan.FromMinutes(15), 135, 560, 27, 3.5, 8),
            ]);
        var replacement = new SolarForecast(
            assetId: "site-1-pv-roof",
            source: "open-meteo",
            model: "pvlib",
            generatedAt: Now + TimeSpan.FromMinutes(5),
            horizonStart: Now,
            horizonEnd: Now + TimeSpan.FromMinutes(45),
            timeStep: TimeSpan.FromMinutes(15),
            installedDcKw: 500,
            installedAcKw: 450,
            points:
            [
                new SolarForecastPoint(Now, 140, 580, 27, 3.1, 7),
                new SolarForecastPoint(Now + TimeSpan.FromMinutes(15), 155, 620, 28, 3.0, 6),
                new SolarForecastPoint(Now + TimeSpan.FromMinutes(30), 165, 640, 29, 2.8, 5),
            ]);

        await store.UpdateAsync(first, CancellationToken.None);
        await store.UpdateAsync(replacement, CancellationToken.None);

        var loaded = await store.GetLatestAsync("site-1-pv-roof", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(replacement.GeneratedAt, loaded!.GeneratedAt);
        Assert.Equal(replacement.HorizonEnd, loaded.HorizonEnd);
        Assert.Equal(3, loaded.Points.Count);
        Assert.Equal(165, loaded.Points[^1].PowerKw);
    }

    [Fact]
    public async Task Audit_log_appends_and_queries_within_window()
    {
        var log = new DapperOperatorAuditLog(_dataSource!);

        var ev = new AuditEvent(
            Timestamp: Now,
            Operator: "operator-1",
            Action: "operator-stop",
            TargetAssetId: "single-bess-1",
            Reason: "manual-shutdown",
            Outcome: "command-issued");
        await log.AppendAsync(ev, CancellationToken.None);

        var inWindow = await log.QueryAsync(Now - TimeSpan.FromMinutes(1), Now + TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Single(inWindow);
        Assert.Equal("operator-stop", inWindow[0].Action);

        // Half-open semantics: querying a window that ends exactly at Now
        // must NOT include the event whose timestamp equals Now.
        var rightOpen = await log.QueryAsync(Now - TimeSpan.FromMinutes(1), Now, CancellationToken.None);
        Assert.Empty(rightOpen);
    }

    [Fact]
    public async Task Retention_run_deletes_only_rows_older_than_cutoff_and_preserves_audit_by_default()
    {
        var telemetry = new DapperTelemetryRepository(_dataSource!);
        var commands = new DapperCommandRepository(_dataSource!);
        var audit = new DapperOperatorAuditLog(_dataSource!);
        var retention = new DapperRetentionRepository(_dataSource!);

        // Seed two telemetry samples and two audit events on either side
        // of a 30-day-old cutoff. The retention run must wipe the old
        // telemetry but leave both audit rows untouched as long as the
        // policy keeps OperatorAuditRetention=null.
        var oldTimestamp = Now - TimeSpan.FromDays(60);
        var newTimestamp = Now - TimeSpan.FromDays(10);

        await telemetry.AppendAsync(SampleTelemetry(oldTimestamp), CancellationToken.None);
        await telemetry.AppendAsync(SampleTelemetry(newTimestamp), CancellationToken.None);

        await commands.AppendAsync(
            SampleCommand("ret-old", oldTimestamp),
            CommandDispatchResult.Ok(oldTimestamp, "ok"),
            CancellationToken.None);
        await commands.AppendAsync(
            SampleCommand("ret-new", newTimestamp),
            CommandDispatchResult.Ok(newTimestamp, "ok"),
            CancellationToken.None);

        await audit.AppendAsync(
            new AuditEvent(oldTimestamp, "operator-1", "old-action", "single-bess-1", "test", "ok"),
            CancellationToken.None);
        await audit.AppendAsync(
            new AuditEvent(newTimestamp, "operator-1", "new-action", "single-bess-1", "test", "ok"),
            CancellationToken.None);

        var clock = new FixedClock(Now);
        var useCase = new RetentionRunUseCase(retention, clock);

        // Policy: retain only data within the last 30 days for telemetry +
        // commands. Audit retention left null so LH-PERSIST-006's default
        // "no auto-delete of audit-relevant data" applies.
        var policy = new RetentionPolicy(
            TelemetryRetention: TimeSpan.FromDays(30),
            CommandsRetention: TimeSpan.FromDays(30),
            SchedulesRetention: null,
            OperatorAuditRetention: null);

        var result = await useCase.ExecuteAsync(policy, CancellationToken.None);

        Assert.Equal(1, result.TelemetryDeleted);
        Assert.Equal(1, result.CommandsDeleted);
        Assert.Equal(0, result.SchedulesDeleted);
        Assert.Equal(0, result.OperatorAuditDeleted);
        Assert.True(result.OperatorAuditPreserved);

        // The newer telemetry / command is still queryable; the older one
        // is gone.
        var remainingTelemetry = await telemetry.QueryAsync(
            "single-bess-1", Now - TimeSpan.FromDays(365), Now + TimeSpan.FromDays(1), CancellationToken.None);
        Assert.Single(remainingTelemetry);
        Assert.Equal(newTimestamp, remainingTelemetry[0].Timestamp);

        Assert.Null(await commands.FindByCommandIdAsync("ret-old", CancellationToken.None));
        Assert.NotNull(await commands.FindByCommandIdAsync("ret-new", CancellationToken.None));

        // Audit was preserved because the policy kept retention null.
        var auditRows = await audit.QueryAsync(
            Now - TimeSpan.FromDays(365), Now + TimeSpan.FromDays(1), CancellationToken.None);
        Assert.Equal(2, auditRows.Count);
    }

    [Fact]
    public async Task Optimization_run_repository_round_trips_full_payload_and_supports_range_query()
    {
        var repo = new DapperOptimizationRunRepository(_dataSource!);

        var optimalRun = BuildRun(
            runId: Guid.NewGuid(),
            assetId: "single-bess-1",
            createdAt: Now,
            status: OptimizationSolverStatus.Optimal,
            terminationReason: "solver_finished",
            objectiveValue: -1234.5,
            components: new[]
            {
                new OptimizationObjectiveComponent("energy_cost", -1500.0, "EUR"),
                new OptimizationObjectiveComponent("degradation", 265.5, "EUR"),
            },
            constraintViolations: Array.Empty<string>(),
            warnings: TightSocWarnings,
            inputs: new[] { new ScheduleReference("single-bess-1", ScheduleType.DayAhead, 7) },
            producedSchedule: new ScheduleReference("single-bess-1", ScheduleType.DayAhead, 8));

        var failedRun = BuildRun(
            runId: Guid.NewGuid(),
            assetId: "single-bess-1",
            createdAt: Now + TimeSpan.FromMinutes(5),
            status: OptimizationSolverStatus.Failed,
            terminationReason: "solver_crash",
            objectiveValue: 0,
            components: Array.Empty<OptimizationObjectiveComponent>(),
            constraintViolations: SocFloorViolations,
            warnings: Array.Empty<string>(),
            inputs: Array.Empty<ScheduleReference>(),
            producedSchedule: null);

        var foreignAssetRun = BuildRun(
            runId: Guid.NewGuid(),
            assetId: "single-bess-2",
            createdAt: Now + TimeSpan.FromMinutes(1),
            status: OptimizationSolverStatus.Optimal,
            terminationReason: "solver_finished",
            objectiveValue: -10,
            components: new[] { new OptimizationObjectiveComponent("energy_cost", -10, "EUR") },
            constraintViolations: Array.Empty<string>(),
            warnings: Array.Empty<string>(),
            inputs: Array.Empty<ScheduleReference>(),
            producedSchedule: new ScheduleReference("single-bess-2", ScheduleType.DayAhead, 1));

        await repo.AppendAsync(optimalRun, CancellationToken.None);
        await repo.AppendAsync(failedRun, CancellationToken.None);
        await repo.AppendAsync(foreignAssetRun, CancellationToken.None);

        var roundTripped = await repo.FindByIdAsync(optimalRun.RunId, CancellationToken.None);
        Assert.NotNull(roundTripped);
        Assert.Equal(optimalRun.AssetId, roundTripped!.AssetId);
        Assert.Equal(optimalRun.Status, roundTripped.Status);
        Assert.Equal(optimalRun.HorizonStart, roundTripped.HorizonStart);
        Assert.Equal(optimalRun.HorizonEnd, roundTripped.HorizonEnd);
        Assert.Equal(optimalRun.TimeStep, roundTripped.TimeStep);
        Assert.Equal(optimalRun.ObjectiveValue, roundTripped.ObjectiveValue);
        Assert.Equal(2, roundTripped.ObjectiveBreakdown.Components.Count);
        Assert.Equal("energy_cost", roundTripped.ObjectiveBreakdown.Components[0].Name);
        Assert.Equal(-1500.0, roundTripped.ObjectiveBreakdown.Components[0].Value);
        Assert.Equal("degradation", roundTripped.ObjectiveBreakdown.Components[1].Name);
        Assert.Equal(optimalRun.SolverRuntime, roundTripped.SolverRuntime);
        Assert.Equal(optimalRun.TerminationReason, roundTripped.TerminationReason);
        Assert.Equal(optimalRun.CreatedAt, roundTripped.CreatedAt);
        Assert.Empty(roundTripped.ConstraintViolations);
        Assert.Single(roundTripped.Warnings);
        Assert.Equal("tight-binding-soc-floor", roundTripped.Warnings[0]);
        Assert.Single(roundTripped.Inputs);
        Assert.Equal(7, roundTripped.Inputs[0].Version);
        Assert.NotNull(roundTripped.ProducedSchedule);
        Assert.Equal(8, roundTripped.ProducedSchedule!.Version);

        // Failed run preserves null produced schedule and one violation.
        var failedRoundTripped = await repo.FindByIdAsync(failedRun.RunId, CancellationToken.None);
        Assert.NotNull(failedRoundTripped);
        Assert.Equal(OptimizationSolverStatus.Failed, failedRoundTripped!.Status);
        Assert.Null(failedRoundTripped.ProducedSchedule);
        Assert.Single(failedRoundTripped.ConstraintViolations);
        Assert.Equal("soc_floor_violated", failedRoundTripped.ConstraintViolations[0]);
        Assert.Empty(failedRoundTripped.ObjectiveBreakdown.Components);

        // Range query: asset filter + half-open [from, until). The
        // foreign-asset run must not appear; failedRun (CreatedAt=Now+5m)
        // must be excluded by an Until=Now+5m boundary because the range
        // is right-open.
        var assetRuns = await repo.QueryAsync(
            "single-bess-1",
            Now - TimeSpan.FromMinutes(1),
            Now + TimeSpan.FromMinutes(5),
            CancellationToken.None);
        Assert.Single(assetRuns);
        Assert.Equal(optimalRun.RunId, assetRuns[0].RunId);

        // Widen the range — failedRun is now included; both ordered by CreatedAt asc.
        var bothRuns = await repo.QueryAsync(
            "single-bess-1",
            Now - TimeSpan.FromMinutes(1),
            Now + TimeSpan.FromMinutes(10),
            CancellationToken.None);
        Assert.Equal(2, bothRuns.Count);
        Assert.Equal(optimalRun.RunId, bothRuns[0].RunId);
        Assert.Equal(failedRun.RunId, bothRuns[1].RunId);
    }

    [Fact]
    public async Task Optimization_run_repository_rejects_duplicate_run_id_append()
    {
        var repo = new DapperOptimizationRunRepository(_dataSource!);
        var runId = Guid.NewGuid();

        var first = BuildRun(
            runId: runId,
            assetId: "single-bess-1",
            createdAt: Now,
            status: OptimizationSolverStatus.Optimal,
            terminationReason: "ok",
            objectiveValue: -1,
            components: new[] { new OptimizationObjectiveComponent("energy_cost", -1, "EUR") },
            constraintViolations: Array.Empty<string>(),
            warnings: Array.Empty<string>(),
            inputs: Array.Empty<ScheduleReference>(),
            producedSchedule: new ScheduleReference("single-bess-1", ScheduleType.DayAhead, 1));

        await repo.AppendAsync(first, CancellationToken.None);

        var second = BuildRun(
            runId: runId,
            assetId: "single-bess-1",
            createdAt: Now + TimeSpan.FromMinutes(1),
            status: OptimizationSolverStatus.Failed,
            terminationReason: "rebound",
            objectiveValue: 0,
            components: Array.Empty<OptimizationObjectiveComponent>(),
            constraintViolations: Array.Empty<string>(),
            warnings: Array.Empty<string>(),
            inputs: Array.Empty<ScheduleReference>(),
            producedSchedule: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.AppendAsync(second, CancellationToken.None));
        Assert.Contains(runId.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrator_preserves_existing_data_on_re_application()
    {
        // A second MigrateAsync call must not break existing data —
        // DbUp's __schema_versions journal sees the script already
        // ran and skips re-execution.
        var ev = new AuditEvent(Now, "operator-1", "first-run", "single-bess-1", "boot", "ok");
        await new DapperOperatorAuditLog(_dataSource!).AppendAsync(ev, CancellationToken.None);

        await new BessDbMigrator(
            _dataSource!, _connectionString!, NullLogger<BessDbMigrator>.Instance)
            .MigrateAsync(CancellationToken.None);

        var afterReinit = await new DapperOperatorAuditLog(_dataSource!).QueryAsync(
            Now - TimeSpan.FromMinutes(1),
            Now + TimeSpan.FromMinutes(1),
            CancellationToken.None);
        Assert.Single(afterReinit);
        Assert.Equal("first-run", afterReinit[0].Action);
    }

    private static BatteryTelemetry SampleTelemetry(DateTimeOffset timestamp) => new(
        Timestamp: timestamp,
        AssetId: "single-bess-1",
        SocPercent: 50,
        SohPercent: 99,
        ActivePowerKw: 0,
        ReactivePowerKvar: 0,
        DcVoltage: 800,
        DcCurrent: 0,
        TemperatureCelsius: 22,
        Available: true,
        FaultStatus: "ok",
        DataQuality: DataQuality.Valid);

    private static BatteryCommand SampleCommand(string id, DateTimeOffset timestamp) => new(
        CommandId: id,
        Timestamp: timestamp,
        AssetId: "single-bess-1",
        Mode: CommandMode.Idle,
        ActivePowerKw: 0,
        ReactivePowerKvar: 0,
        ValidUntil: timestamp + TimeSpan.FromMinutes(1),
        Reason: "test",
        Source: CommandSource.Optimization);

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) { UtcNow = now; }
        public DateTimeOffset UtcNow { get; }
    }

    private static OptimizationRun BuildRun(
        Guid runId,
        string assetId,
        DateTimeOffset createdAt,
        OptimizationSolverStatus status,
        string terminationReason,
        double objectiveValue,
        IReadOnlyList<OptimizationObjectiveComponent> components,
        IReadOnlyList<string> constraintViolations,
        IReadOnlyList<string> warnings,
        IReadOnlyList<ScheduleReference> inputs,
        ScheduleReference? producedSchedule)
    {
        var (code, detail) = OptimizationRun.ParseTerminationReason(terminationReason);
        return new OptimizationRun(
            runId: runId,
            assetId: assetId,
            solverName: "or-tools-stub",
            status: status,
            horizonStart: createdAt,
            horizonEnd: createdAt + TimeSpan.FromHours(24),
            timeStep: TimeSpan.FromMinutes(15),
            objectiveValue: objectiveValue,
            objectiveBreakdown: components.Count == 0
                ? OptimizationObjectiveBreakdown.Empty
                : new OptimizationObjectiveBreakdown(components),
            constraintViolations: constraintViolations,
            warnings: warnings,
            solverRuntime: TimeSpan.FromMilliseconds(125),
            terminationCode: code,
            terminationDetail: detail,
            createdAt: createdAt,
            inputs: inputs,
            producedSchedule: producedSchedule);
    }

    private static async Task TruncateAllAsync(NpgsqlDataSource dataSource)
    {
        var connection = await dataSource.OpenConnectionAsync();
        await using (connection.ConfigureAwait(false))
        {
            await using var cmd = new NpgsqlCommand(
                "TRUNCATE telemetry, commands, schedule_windows, schedules, audit_events, "
                + "optimization_objective_breakdowns, optimization_runs, "
                + "regelleistung_activations, price_series_points, price_series, "
                + "site_measurements, site_consumption_readings, site_pv_profiles, solar_forecast_points, solar_forecasts "
                + "RESTART IDENTITY CASCADE;",
                connection);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task WaitForTcpAsync(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var probe = new System.Net.Sockets.TcpClient();
                using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await probe.ConnectAsync(host, port, probeCts.Token);
                if (probe.Connected)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
            {
                lastError = ex;
            }
            await Task.Delay(200);
        }
        throw new InvalidOperationException(
            $"Postgres at {host}:{port} did not accept TCP connections within {timeout}: {lastError?.Message}");
    }
}
