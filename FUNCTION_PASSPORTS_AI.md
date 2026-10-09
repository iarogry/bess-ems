# Function Passports for AI

Purpose: a compact navigation registry for key execution paths. This file does
not replace reading the referenced code before editing it.

Generated: `2026-07-18T19:00:33+03:00`

Git baseline: `ac08e912975ec63d631f8d268385f84fc0225378`

The working tree had 173 changed/untracked paths when this baseline was
created. Therefore `base_commit` alone never proves freshness.

## Status And Freshness

- `✓`: implementation and stated contract have concrete verification evidence.
- `⚠`: navigation is useful, but verification is old, partial, or not rerun.
- `✗`: passport is known to disagree with live code and must not guide edits.
- `content_sha256`: SHA-256 of the complete live file when indexed.
- Before relying on a passport, compare its hash and line anchors with the
  live file. A mismatch changes the effective status to `⚠` until reviewed.
- Line ranges are navigation anchors, not edit boundaries.

## Coverage

| Area | Key path | Status |
|---|---|---|
| Host composition | `BessHostBuilder.BuildApp` | ✓ |
| FusionSolar site telemetry | `PollAsync` -> `getStationRealKpi` | ✓ |
| ASKUE consumption | `CollectAsync` | ✓ |
| Deye battery telemetry | `ReadAsync` | ✓ |
| Internal orchestration | `StartAsync` | ✓ |
| Site balance | `SiteBalanceCalculator.Calculate` | ✓ |
| Schedule optimization | `OrToolsScheduleOptimizer.OptimizeAsync` | ✓ |
| Site status HTTP API | `MapSiteTelemetryStatus` | ✓ |

## FP-HOST-001

```yaml
symbol: BatteryEms.Host.BessHostBuilder.BuildApp
signature: public static WebApplication BuildApp(string[] args)
file: src/host/BatteryEms.Host/BessHostBuilder.cs
lines: 44-55
base_commit: ac08e912975ec63d631f8d268385f84fc0225378
content_sha256: 4313d295d1096db025ed3b87b13ae0de2a3208b7e9528c84383a470f2f74915b
status: ✓
effects: [config_read, di_registration, database_migration, runtime_seed]
calls:
  - LoadHostOptions@56
  - ConfigureHostBuilder@61
  - ConfigurePersistence@103
  - ConfigureOptimization@111
  - ConfigureSiteTelemetry@193
  - ConfigureSiteConsumption@216
  - ConfigureIoAdapters@118
called_by:
  - BatteryEms.Host.Program
error_contract: Invalid startup configuration fails application construction; migration and seed failures propagate.
returns: Fully configured WebApplication; it is not started by this function.
verification: ArchitectureTests 29/29 on 2026-07-18; host and all registered adapter projects compiled on .NET 10.0.302 x64.
```

Read before changing: `BessHostOptions.cs`, `BessConfigurationBootstrap.cs`,
the selected adapter registration extension, and architecture composition
tests.

## FP-FUSION-001

```yaml
symbol: BatteryEms.Adapters.FusionSolar.FusionSolarSiteTelemetrySource.PollAsync
signature: public Task<int> PollAsync(DateTimeOffset now, CancellationToken cancellationToken)
file: src/adapters/driven/BatteryEms.Adapters.FusionSolar/FusionSolarSiteTelemetrySource.cs
lines: 54-57
base_commit: ac08e912975ec63d631f8d268385f84fc0225378
content_sha256: 832375bafa080ec78d6c8c8a3899525fb9aeacede29d992b67ac7aabe32211b9
status: ✓
effects: [http_login, http_read, memory_snapshot_write, structured_log]
calls:
  - PollStationsAsync@79
  - GetValidTokenAsync@181
  - ReadRealTimeStationTelemetryAsync@213
  - PostEnvelopeAsync@232
called_by:
  - FusionSolarSiteTelemetryHostedService.ExecuteAsync@42
error_contract: Cancellation propagates; HTTP/JSON/timeout/API failures are logged and return 0; successful result is the number of usable stations.
returns: Count of station readings aggregated into site telemetry.
verification: BatteryEms.Adapters.FusionSolar.Tests 7/7; production cycle updated 4 stations on 2026-07-17 without 407.
```

Behavior contract:

- Sends all configured station codes in one `/thirdData/getStationRealKpi`
  request every configured cycle.
- Reuses one XSRF token for 29 minutes and serializes requests.
- Selects the newest duplicate per station, reads `active_power` in kW, sums
  valid stations, and writes one site telemetry snapshot for the configured
  EMS asset.
- Must not be changed back to per-station login cycles or
  `getKpiStationHour` without new evidence and tests.

## FP-FUSION-002

```yaml
symbol: BatteryEms.Adapters.FusionSolar.FusionSolarSiteTelemetryHostedService.ExecuteAsync
signature: protected override async Task ExecuteAsync(CancellationToken stoppingToken)
file: src/adapters/driven/BatteryEms.Adapters.FusionSolar/FusionSolarSiteTelemetryHostedService.cs
lines: 32-56
base_commit: ac08e912975ec63d631f8d268385f84fc0225378
content_sha256: e222da2058ea307355416e49934e298ac2a9c9cfe2a5b1249dc3fd082588998d
status: ✓
effects: [timer_wait, external_poll, structured_log]
calls:
  - FusionSolarSiteTelemetrySource.PollAsync@42
called_by:
  - Microsoft.Extensions.Hosting
error_contract: Host cancellation ends the loop; arbitrary cycle failures are logged and the next timer tick remains eligible.
returns: Background task that normally completes only on host shutdown.
verification: Production interval 300 seconds and successful 4-station cycle verified 2026-07-17.
```

## FP-ASKUE-001

```yaml
symbol: BatteryEms.Adapters.Askue.AskueSiteConsumptionCollector.CollectAsync
signature: public async Task<int> CollectAsync(DateOnly date, CancellationToken cancellationToken)
file: src/adapters/driven/BatteryEms.Adapters.Askue/AskueSiteConsumptionCollector.cs
lines: 46-71
base_commit: ac08e912975ec63d631f8d268385f84fc0225378
content_sha256: fa6c22142fd0ff061d903666a5cdae51b4f83f49c082b08203d2bb6e3fc94a54
status: ✓
effects: [http_read, consumption_store_write, structured_log]
calls:
  - GetPointsAsync@73
  - FetchReadingsForPointAsync@112
  - ISiteConsumptionStore.ImportAsync
called_by:
  - AskueConsumptionHostedService.ExecuteAsync@42
error_contract: HTTP/JSON/cancellation failures propagate to the hosted-service boundary; empty point/profile sets produce zero imported readings.
returns: Total imported canonical consumption readings.
verification: BatteryEms.Adapters.Askue.Tests 3/3 on 2026-07-18; live polling is opt-in with BESS_RUN_LIVE_ASKUE_TESTS=1.
```

Canonical conversion reminder: source interval values become energy using
`source_value * value_multiplier * interval_seconds / 3600` in the site
balance path. Do not silently apply `/points.scale` as a physical conversion.

Operational archive contract:

- Treat the ASKUE server as the only source of truth for historical export.
- The current clean-archive scope is `2026-01-01` through `2026-07-19`
  inclusive.
- Export day-by-day and merge into one final CSV.
- Deduplicate the final archive by `point_id + interval_start_local` or,
  equivalently for canonical UTC rows, `point_id + timestamp`.
- Runtime browser cookies may be required for `/meter/{point_id}.html`; use
  runtime injection only and never persist or document live cookie values.
- Local `tmp/*.csv` files are working artifacts, not authoritative storage.
- Persisted app imports land in `site_consumption_readings` and are upserted by
  `(site_id, source, point_id, timestamp)`, so canonical stored rows do not
  duplicate the same interval for one source.

## FP-DEYE-001

```yaml
symbol: BatteryEms.Adapters.DeyeCloud.DeyeCloudTelemetrySource.ReadAsync
signature: public async IAsyncEnumerable<BatteryTelemetry> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
file: src/adapters/driven/BatteryEms.Adapters.DeyeCloud/DeyeCloudTelemetrySourc.cs
lines: 74-180
base_commit: ac08e912975ec63d631f8d268385f84fc0225378
content_sha256: 02fdf1a3f054d28950b6a52f33336ae303b9bb74f69d28726c9709d8b1078743
status: ✓
effects: [http_login, http_read, memory_snapshot_write, timer_wait, structured_log]
calls:
  - EnsureTokenAsync@207
  - PostEnvelopeAsync@268
  - PostJsonAsync@257
  - UpdateSiteTelemetry@370
called_by:
  - telemetry ingestion hosted service through IBatteryTelemetrySource
error_contract: Produces invalid telemetry on bounded source failures, preserves cancellation, retries according to adapter options, and continues polling.
returns: Async stream of normalized BatteryTelemetry snapshots.
verification: BatteryEms.Adapters.DeyeCloud.Tests 2/2 and ArchitectureTests 29/29 on 2026-07-18; OS-managed TLS negotiation compiles without CA5398.
```

## FP-ORCH-001

```yaml
symbol: BatteryEms.Application.Orchestration.DefaultOrchestrationUseCase.StartAsync
signature: public async Task<OrchestrationRun> StartAsync(OrchestrationStartCommand command, CancellationToken cancellationToken)
file: src/hexagon/BatteryEms.Application/Orchestration/DefaultOrchestrationUseCase.cs
lines: 38-138
base_commit: ac08e912975ec63d631f8d268385f84fc0225378
content_sha256: 17ee945df16b68f8a0e19cdbc0957eedeadcdec599aa2f4aca7ff23e21ece476
status: ✓
effects: [orchestration_lock, run_store_write, data_balance_read, module_execution, audit_state_write]
calls:
  - IOrchestrationLockStore.TryAcquireAsync
  - IOrchestrationRunStore.TryStartAsync
  - IDataReadinessPolicy.Evaluate
  - ExecuteModulesAsync@140
  - IOrchestrationRunStore.CompleteAsync
called_by:
  - orchestration driving ports; production scheduler/API wiring remains a follow-up
error_contract: Duplicate or blocked starts return recorded non-active runs; cancellation propagates; lock release occurs in finally.
returns: Persisted final or duplicate/blocked OrchestrationRun.
verification: BatteryEms.Application.Tests 399/399 on 2026-07-18, including OrchestrationUseCaseTests.
```

## FP-SITE-001

```yaml
symbol: BatteryEms.Application.Site.SiteBalanceCalculator.Calculate
signature: public static SiteBalanceResult Calculate(SiteBalanceCalculationRequest request)
file: src/hexagon/BatteryEms.Application/Site/SiteBalance.cs
lines: 132-202
base_commit: ac08e912975ec63d631f8d268385f84fc0225378
content_sha256: 74fe7b4521fc262534254f40d98994383a1b16869927396adc4904d7bc33f1b7
status: ✓
effects: [none]
calls:
  - SiteBalanceCalculationRequest.EnsureValid
  - BuildGenerationBalances@204
  - AddCrossCheckWarnings@225
called_by:
  - DefaultSiteBalanceUseCase.CalculateAsync@358
error_contract: Invalid request/configuration throws validation exceptions; missing source values produce warnings/partial totals according to quality rules.
returns: Deterministic SiteBalanceResult with totals, generation balances, quality, and warnings.
verification: BatteryEms.Application.Tests 399/399 on 2026-07-18, including SiteBalanceCalculatorTests and SiteBalanceUseCaseTests.
```

## FP-OPT-001

```yaml
symbol: BatteryEms.Adapters.Optimization.OrTools.OrToolsScheduleOptimizer.OptimizeAsync
signature: public Task<ScheduleOptimizationResult> OptimizeAsync(ScheduleOptimizationRequest request, CancellationToken cancellationToken)
file: src/adapters/driven/BatteryEms.Adapters.Optimization/OrTools/OrToolsScheduleOptimizer.cs
lines: 74-99
base_commit: ac08e912975ec63d631f8d268385f84fc0225378
content_sha256: 39563c84bfda950fad96fb29002eedfe5cdc0aee79ad96e878c11e9c1bfb1efb
status: ✓
effects: [solver_execution, structured_log]
calls:
  - ScheduleOptimizationRequest.EnsureValid
  - Solve@101
  - BuildSolutionResult@267
  - BuildNonSolutionResult@320
  - BuildFailedResult@337
called_by:
  - Application optimization use case through IScheduleOptimizer
error_contract: Validation failures are represented by failed optimization results where handled; cancellation is checked before synchronous solve; solver statuses map to explicit result states.
returns: ScheduleOptimizationResult containing schedule or bounded failure diagnostics and optimization run metadata.
verification: BatteryEms.Adapters.Optimization.Tests 101/101 on 2026-07-18; live ENTSO-E scratch flow is opt-in with BESS_RUN_LIVE_ENTSOE_TESTS=1.
```

## FP-API-001

```yaml
symbol: BatteryEms.Api.Endpoints.SiteTelemetryEndpoints.MapSiteTelemetryStatus
signature: public static IEndpointRouteBuilder MapSiteTelemetryStatus(this IEndpointRouteBuilder routes)
file: src/adapters/driving/BatteryEms.Api/Endpoints/SiteTelemetryEndpoints.cs
lines: 12-37
base_commit: ac08e912975ec63d631f8d268385f84fc0225378
content_sha256: 0280ab2fa26ce466d2ae6e53ecb1bdd243bfbac5f865a12edb5786bd27abc59a
status: ✓
effects: [http_route_registration, memory_snapshot_read]
calls:
  - ISiteStatusQuery.FindAsync@22
called_by:
  - BatteryEms.Api.Program.BuildApp@67
  - BatteryEms.Host.BessHostBuilder.ConfigureApp@308
error_contract: Unknown asset returns HTTP 404; known asset returns HTTP 200 with nullable telemetry/quality fields.
returns: The same route builder after registering GET /site/{assetId}/status.
verification: Production endpoint returned Valid telemetry on 2026-07-17.
```

## Unindexed Key Areas

These are outside the current passport registry and must be read from live code
until passports are added: control cycle,
command safety/limiter, persistence migrations and stores, ENTSO-E import,
Open-Meteo forecasting, Modbus/MQTT/OPC-UA writes, NativeInterop fallback,
operator-stop path, schedule imports, and optimization API request mapping.
