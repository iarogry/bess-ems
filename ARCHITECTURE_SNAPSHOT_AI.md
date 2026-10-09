# Architecture Snapshot for AI

Purpose: fast architectural orientation. This snapshot does not override
`spec/architecture.md`, ADRs, architecture tests, or live project references.

Generated: `2026-07-18T18:29:22+03:00`

Git baseline: `ac08e912975ec63d631f8d268385f84fc0225378`

Working tree at generation: dirty, 173 changed/untracked paths. Read changed
files before making decisions.

## Global Execution Formula

```text
external trigger
  -> driving adapter (HTTP API / Worker / hosted collector)
  -> Application driving port / use case
  -> Domain policy and value objects
  -> Application driven ports
  -> driven adapters (Postgres / protocols / cloud / solver)
  -> normalized result or explicit degraded/failure state
```

Optimization and control are separate formulas:

```text
prices + forecasts + constraints + battery state
  -> schedule optimization
  -> versioned desired Schedule
  != direct device command

telemetry snapshot + active schedule + safety/state machine + limiter
  -> bounded BatteryCommand
  -> command sink adapter
```

Site telemetry formula in the current production path:

```text
FusionSolar login (token reused 29 min)
  -> one getStationRealKpi request for 4 stations every 5 min
  -> newest active_power per station
  -> sum PV kW
  -> ISiteTelemetryStore(single-bess-1)
  -> GET /site/{assetId}/status
  -> operator UI / site preparation
```

## Module Topology

```text
BatteryEms.Domain
  <- BatteryEms.Application
       <- driving adapters: BatteryEms.Api, BatteryEms.Worker
       <- driven adapters: Modbus, MQTT, OPC-UA, Persistence, Telemetry,
          Optimization, NativeInterop, DeyeCloud, FusionSolar, ASKUE,
          ENTSO-E, OpenMeteo

BatteryEms.Host / BatteryEms.Infrastructure
  -> composition roots that select and wire concrete implementations
```

`BatteryEms.Application` is intentionally one project with namespaces for
Realtime, Control, Markets, Optimization, Site, Forecasting, and
Orchestration. Do not create a new Application project without satisfying ADR
0011 split triggers.

## Runtime State And Cache Map

| State | Authority | Durability | Main readers/writers |
|---|---|---|---|
| Battery telemetry snapshot | `ISnapshotStore` | memory | telemetry source -> control/status |
| Site power telemetry | `ISiteTelemetryStore` | memory | FusionSolar/Deye -> site status/preparation |
| ASKUE consumption | `ISiteConsumptionStore` | PostgreSQL when persistence enabled | ASKUE -> balance/preparation |
| Price series | `IPriceSeriesSource` / persistence sink | external + PostgreSQL | ENTSO-E/API -> optimizer |
| Schedules/commands/audit | Application ports | PostgreSQL in production | optimization/control/API |
| Orchestration runs/steps/locks/balances | orchestration stores | PostgreSQL target; in-memory test stores exist | orchestrator/API/worker |
| Solar forecasts/PV profiles | forecast/profile stores | PostgreSQL | OpenMeteo -> site planning |

Memory snapshot freshness is part of the contract. Do not treat a non-null
snapshot as valid without checking quality and age. Production site telemetry
max age is currently 40 minutes; FusionSolar polls every 5 minutes.

## Identity Boundaries

- `site_id`: site-level data, balances, forecasts, and orchestration.
- `asset_id`: battery telemetry, schedules, commands, and command authority.
- `instrument_id`: source-native meter/device identity; never substitutes for
  `site_id` or `asset_id`.
- Current production uses the single battery asset `single-bess-1` and
  aggregates four FusionSolar plants into its site telemetry view.

## Dependency Rules

Allowed:

```text
Domain -> no project references
Application -> Domain only
Driving adapter -> Application and optionally Domain
Driven adapter -> Application and optionally Domain
Host/Infrastructure -> composition-time concrete modules
```

Forbidden:

```text
Domain/Application -> adapters, Host, Infrastructure, HTTP/database frameworks
adapter -> another adapter
driving adapter -> driven adapter
driven adapter -> driving adapter
optimizer -> device write
source adapter -> orchestration business decision
agent/proposal context -> secrets
site/orchestration advisory path -> direct battery command
```

Architecture tests in `tests/BatteryEms.ArchitectureTests` enforce the core
dependency rule and framework taboos. Extend those tests when introducing a new
module boundary; do not rely on this prose alone.

## Safety Invariants

1. Uncertainty fails closed; it never defaults to active control.
2. Optimizers produce desired plans, not device writes.
3. State machine, limiter, data quality, freshness, and approval gates remain
   authoritative for commands.
4. Edge/BMS/PCS protections remain outside Docker EMS and are not replaced by
   supervisory logic.
5. Secrets must not enter logs, measurements, orchestration records, reports,
   handoffs, or AI documents.
6. Retries must be bounded and observable; cancellation must propagate.
7. Source failures are isolated where possible and represented as explicit
   partial/degraded/source-error states.

## Storage And Units

- Runtime persistence: PostgreSQL with Dapper/Npgsql and forward-only DbUp
  migrations under `Migrations/RunOnce`.
- Canonical units include `kW`, `kWh`, `kvar`, `kvarh`, `V`, `A`, `%`,
  `degC`, `W/m2`, `UAH/MWh`, `UAH/kWh`, and `UAH`.
- ASKUE interval conversion:
  `source_value * value_multiplier * interval_seconds / 3600`.
- FusionSolar `getStationRealKpi.dataItemMap.active_power` is current kW.
- Deye source power is normalized from W to kW before internal use.

## Navigation Order Before Changes

1. Read the matching entry in `FUNCTION_PASSPORTS_AI.md`.
2. Verify file hash and line anchors against live code.
3. Read the complete target function and its direct callers/callees.
4. Check this snapshot and `spec/architecture.md` for boundary constraints.
5. Read the newest relevant handoff from `STATE_INDEX_AI.md`.
6. Inspect tests that pin the contract.
7. Edit code, run focused tests, update passport/hash/status, and write a new
   handoff.

## Known Architectural Gaps

- Orchestration Application/storage slice exists, but complete production
  Worker/API module scheduling remains follow-up work.
- Some source adapters still own hosted polling schedules; the documented
  target is centralized orchestration ownership where practical.
- Function passport coverage is intentionally partial. Unindexed areas must be
  discovered from live code, not inferred from naming.

