# BESS-EMS Internal Orchestrator Plan

Status: first implementation slice delivered; Worker/API module execution and
agent/control flows remain follow-up work.

Date: 2026-06-06.

## Goal

Add an internal orchestrator inside `bess-ems` that decides which modules must
run, in which order, with which data dependencies, and under which retry,
idempotency, freshness, and approval rules.

The orchestrator is the coordinator. It is not the battery calculation engine.

It may trigger:

- data collection;
- data normalization;
- site settings preparation;
- day-ahead optimization;
- site advisory reports;
- agent context/proposals;
- later, approved control commands.

It must not bypass:

- deterministic safety checks;
- data quality validation;
- idempotency/locking;
- operator approval gates for operational actions;
- the existing battery engine boundary.

## Non-Negotiable Rules

1. The existing battery engine remains isolated.
2. The orchestrator can initiate optimization, but it must call existing use
   cases/adapters rather than changing optimizer math.
3. Control commands are future scope and require an explicit approval gate.
4. All orchestration state is persisted in PostgreSQL.
5. Duplicate work must be prevented.
6. All module outputs must carry data quality/freshness status.
7. Missing critical data blocks the relevant orchestration cycle.
8. Missing informational/advisory data retries without blocking unrelated
   critical work.
9. The agent is included from the beginning, but starts as advisory/audited.
10. Secrets never enter orchestration records, agent context, logs, reports, or
    metadata.
11. Adapter projects must not contain orchestration business decisions.
12. Optimization outputs are desired plans only; technical execution remains
    subject to state machine, limiter, safety, and future approval gates.
13. Site orchestration is keyed by `site_id`; battery command ownership remains
    keyed by `asset_id`.
14. Any uncertainty in operational readiness must fail closed, not default to
    active control.

## Alignment With Existing Architecture

This module follows the existing BESS-EMS architecture rather than introducing
a new architectural style.

Traceability:

| Existing rule | Orchestrator consequence |
|---|---|
| `AR-P-001` strict separation of market optimization and technical control | Orchestration may start optimization, but may not dispatch optimization output directly. |
| `AR-P-002` modular layered architecture | Orchestration lives in Application with adapters for persistence/API/worker scheduling. |
| `AR-P-003` adapters contain no business decisions | Source adapters collect data only; readiness, criticality, retry and workflow decisions live in Application orchestration. |
| `AR-P-004` optimizers never write to devices | Orchestrated optimization creates schedules/reports/proposals, not device writes. |
| `AR-P-005` unified internal models | Orchestration consumes `SiteMeasurementReading`, `SitePreparedSettings`, price series, schedules and data quality records. |
| `AR-P-007` safe fallback on uncertainty | Missing/stale/invalid critical data blocks dependent workflows. |
| `AR-P-011` hexagonal dependency rule | Application defines ports; driven adapters implement PostgreSQL and external integration. |
| ADR 0011 Application monolithic module | Add `BatteryEms.Application.Orchestration` namespace, not a new Application csproj. |
| ADR 0007 shared worker with per-asset fan-out | Orchestration must isolate site/asset failures and avoid one failed module stopping unrelated work. |
| ADR 0008 edge boundary | Future commands are supervisory/audited and must not claim hard real-time or replace BMS/PCS/hardware protection. |
| ADR 0001 persistence style | Runtime persistence uses Dapper/Npgsql and forward-only DbUp SQL migrations. |

Balanced design rule:

```text
Worker/API drive orchestration.
Application orchestration decides workflow.
Driven adapters collect/persist external facts.
Existing use cases execute their own bounded responsibilities.
Control and safety gates remain authoritative for commands.
```

## Placement

The orchestrator lives inside `bess-ems`.

Recommended namespaces:

```text
BatteryEms.Application.Orchestration
BatteryEms.Adapters.Persistence
BatteryEms.Worker or BatteryEms.Host hosted service
BatteryEms.Api endpoints for manual runs/status
```

The first implementation should be application-layer first:

```text
IOrchestrationUseCase
IOrchestrationRunStore
IOrchestrationLockStore
IDataReadinessPolicy
IOrchestrationModule
```

Then wire it into a hosted service and API endpoints.

Boundary placement:

| Responsibility | Project/namespace |
|---|---|
| Records, policies, use case interfaces, workflow decisions | `src/hexagon/BatteryEms.Application/Orchestration` |
| In-memory test stores if needed | `BatteryEms.Application.Orchestration` |
| PostgreSQL stores and migrations | `src/adapters/driven/BatteryEms.Adapters.Persistence` |
| Periodic scheduler / hosted service | `src/adapters/driving/BatteryEms.Worker` or Host wiring |
| Manual/status API endpoints | `src/adapters/driving/BatteryEms.Api` |
| FusionSolar/Deye/ASKUE/ENTSO-E protocol details | Existing driven adapters |

Forbidden dependencies:

```text
Application.Orchestration -> Adapter implementation
FusionSolar/Deye/ASKUE adapters -> Orchestration use case
Optimization adapter -> Orchestration use case
Agent proposal builder -> secrets/config credentials
```

Allowed dependencies:

```text
Worker/API -> IOrchestrationUseCase
IOrchestrationUseCase -> Application ports
Persistence adapter -> orchestration store interfaces
Composition root -> concrete adapter registrations
```

## High-Level Flow

```text
schedule/event/manual trigger
        |
        v
orchestrator acquires idempotency lock
        |
        v
loads site config + module policy
        |
        v
checks data readiness/freshness
        |
        v
runs required modules in dependency order
        |
        v
records module step status in PostgreSQL
        |
        v
builds prepared site settings
        |
        v
runs optimization/advisory workflow when prerequisites are ready
        |
        v
creates agent context/proposal
        |
        v
approval gate before any future command path
```

## Orchestrated Modules

Initial module catalog:

| Module | Purpose | Frequency | Criticality |
|---|---|---:|---|
| `price_import_rdn` | Import ENTSO-E/RDN day-ahead prices. | once/day after market publication | critical for day-ahead optimization |
| `askue_consumption_poll` | Pull site meter/load data. | 15 or 60 min | critical for site balance if load forecast depends on it |
| `deye_cloud_poll` | Pull BESS/site live telemetry. | frequent | critical for battery state when optimization/dispatch uses current SOC |
| `fusionsolar_poll` | Pull PV station KPI data. | frequent/hourly | advisory or critical depending on selected site instruments |
| `site_measurement_normalization` | Convert raw values into common metric/unit shape. | after source poll | critical for site layer |
| `site_settings_preparation` | Build prepared site values from normalized data. | after fresh source data | critical for site advisory/optimization |
| `battery_day_ahead_optimization` | Run existing battery optimizer for next-day horizon. | after prices ready | critical for plan generation |
| `site_advisory_report` | Combine prices, load, PV, grid, battery plan. | after prepared settings and battery plan | advisory initially |
| `agent_context_build` | Build bounded no-secret context for the agent. | after report | advisory |
| `agent_proposal` | Generate/store explanation/proposal. | after context | advisory, approval required for operational proposal |
| `command_activation` | Future approved command execution. | event/approval-driven | critical and gated |

The exact set is per site and per selected instruments. The orchestrator should
not assume every site has every module.

## Trigger Types

The orchestrator must support mixed triggers:

| Trigger | Example | Notes |
|---|---|---|
| `scheduled` | daily RDN import | Cron-like schedule. |
| `poll_interval` | ASKUE every 15/60 min | Fixed interval with lock. |
| `source_event` | new price series imported | Starts downstream dependency chain. |
| `manual` | operator starts run from API | Must use same idempotency rules. |
| `retry` | source failed but is retryable | Backoff based on policy. |
| `approval` | operator approves future command | Explicit event. |

## Planning Horizons

Day-ahead planning:

```text
horizon = next market day
trigger = successful RDN price import
time_step = 1 hour unless request says otherwise
```

Near-real-time preparation:

```text
horizon = rolling current day or last N intervals
trigger = ASKUE/Deye/FusionSolar poll
time_step = 15 min, 30 min, or 1 hour depending on source availability
```

Future intraday:

```text
horizon = remaining day or rolling 24h
trigger = price update, SOC change, site constraint event, operator action
```

## Data Criticality

Every required data group gets a `data_role`.

| Role | Meaning | Orchestrator behavior |
|---|---|---|
| `critical` | Required to produce a valid run. | Stop dependent workflow when missing/invalid/stale. |
| `blocking_safety` | Required for safety decision. | Stop operational workflow, emit safety warning. |
| `advisory` | Improves plan/report but not required for battery-only operation. | Retry, add warning, continue allowed workflows. |
| `informational` | Useful for operator/agent context only. | Retry separately, never block critical optimization. |
| `optional` | Site does not require it for selected instruments. | Ignore if missing. |

Examples:

| Data group | Default role |
|---|---|
| RDN day-ahead prices for day-ahead optimization | `critical` |
| Battery SOC/SOH/current state for operational optimization | `critical` |
| Grid voltage for command execution | `blocking_safety` |
| ASKUE load history for site advisory | `critical` when load forecast is enabled, otherwise `advisory` |
| FusionSolar PV telemetry | `critical` when PV self-consumption/forecast is selected, otherwise `advisory` |
| Deye site load/PV fields | `advisory` if ASKUE/FusionSolar are primary sources |
| Agent explanation | `informational` |

## Data Status Model

Each data balance should have a status record so the system knows whether it
can use, refresh, retry, or block.

Recommended statuses:

| Status | Meaning |
|---|---|
| `unknown` | No check has been performed. |
| `ready` | Fresh enough and valid for its role. |
| `partial` | Some sources succeeded, some failed. |
| `missing` | Required source has no data. |
| `stale` | Data exists but is older than freshness policy. |
| `source_error` | Source returned an API/protocol error. |
| `invalid` | Data failed schema/unit/range validation. |
| `retrying` | Retry is scheduled. |
| `blocked` | Dependent workflow cannot continue. |
| `degraded` | Workflow may continue, but report must show warning. |

Minimum data balance fields:

```text
site_id
data_group
source
instrument_id
role
status
freshness_deadline_utc
last_success_at_utc
last_attempt_at_utc
next_attempt_at_utc
attempt_count
last_error_code
last_error_message
quality_summary
metadata_json
```

No secrets in `metadata_json`.

## Freshness Policy

Each module/data group needs a freshness policy.

Initial defaults:

| Data group | Freshness target | Max stale age | Retry policy |
|---|---:|---:|---|
| RDN day-ahead prices | once after publication | valid for market day | retry every 15 min until available |
| ASKUE 15 min meter data | every 15 min | 45 min | retry every 5 min, then backoff |
| ASKUE hourly meter data | every 60 min | 90 min | retry every 10 min, then backoff |
| Deye telemetry | 1-5 min | 10 min | retry every 1 min, then backoff |
| FusionSolar hourly KPI | hourly | 2 hours | retry every 15 min |
| Site settings prepared values | after source refresh | source dependent | recompute after any source update |
| Battery optimization result | after prices + current SOC | until horizon changes or input hash changes | rerun on input hash change |
| Agent proposal | after advisory report | until report/input hash changes | regenerate on new context |

These values are defaults; site config should be able to override them.

## Dependency Graph

Day-ahead run:

```text
price_import_rdn
        |
        v
data_readiness_check(prices)
        |
        v
site_settings_preparation
        |
        v
battery_day_ahead_optimization
        |
        v
site_advisory_report
        |
        v
agent_context_build
        |
        v
agent_proposal
```

Near-real-time source refresh:

```text
askue_consumption_poll
deye_cloud_poll
fusionsolar_poll
        |
        v
site_measurement_normalization
        |
        v
data_balance_update
        |
        v
site_settings_preparation
        |
        v
if relevant input hash changed:
  advisory refresh / intraday candidate
```

Future control path:

```text
site_advisory_report
        |
        v
agent_proposal
        |
        v
deterministic validation gates
        |
        v
operator approval
        |
        v
command_activation
```

## PostgreSQL Persistence

The orchestrator needs durable state.

Persistence style must match the project:

- use Dapper/Npgsql repositories;
- add forward-only SQL migrations under `Migrations/RunOnce`;
- migrations are applied by the existing `BessDbMigrator` / DbUp path;
- no EF Core migrations;
- no new ORM stack;
- use `timestamptz` with UTC `DateTimeOffset` values;
- include idempotent uniqueness constraints for run and step keys;
- keep audit-relevant rows; do not silently delete orchestration history.

### `orchestration_runs`

One row per logical run.

```text
run_id uuid primary key
site_id text not null
run_type text not null
horizon_start timestamptz null
horizon_end timestamptz null
trigger_type text not null
trigger_ref text null
idempotency_key text not null unique
status text not null
started_at timestamptz not null
completed_at timestamptz null
input_hash text null
output_ref text null
error_code text null
error_message text null
metadata_json jsonb not null default '{}'
```

Run statuses:

```text
queued
running
succeeded
partial
blocked
failed
cancelled
awaiting_approval
```

### `orchestration_steps`

One row per module execution inside a run.

```text
step_id uuid primary key
run_id uuid not null
site_id text not null
module text not null
step_order int not null
idempotency_key text not null unique
status text not null
started_at timestamptz null
completed_at timestamptz null
attempt_count int not null
next_attempt_at timestamptz null
input_hash text null
output_ref text null
data_role text null
data_status text null
error_code text null
error_message text null
metadata_json jsonb not null default '{}'
```

Step statuses:

```text
queued
running
succeeded
partial
retrying
skipped
blocked
failed
```

### `orchestration_locks`

Simple DB-backed lock/idempotency guard for module-level work. This is separate
from the global migration advisory lock.

```text
lock_key text primary key
owner_id text not null
acquired_at timestamptz not null
expires_at timestamptz not null
metadata_json jsonb not null default '{}'
```

Lock rules:

- acquire lock before a module starts;
- lock key includes site, module, horizon/interval, and input hash;
- expired locks may be reclaimed;
- successful work records output and releases lock;
- failed retryable work releases lock and schedules retry.
- lock TTL must be shorter than the maximum retry horizon and longer than the
  expected module runtime.
- lock release must happen in a `finally` path when the process remains alive.

### `orchestration_data_balances`

Current readiness/freshness view per data group.

```text
site_id text not null
data_group text not null
source text not null
instrument_id text not null
role text not null
status text not null
freshness_deadline_utc timestamptz null
last_success_at_utc timestamptz null
last_attempt_at_utc timestamptz null
next_attempt_at_utc timestamptz null
attempt_count int not null
last_error_code text null
last_error_message text null
quality_summary text null
metadata_json jsonb not null default '{}'
primary key (site_id, data_group, source, instrument_id)
```

## Idempotency Keys

Every run and step must have deterministic idempotency keys.

Examples:

```text
run:site-1:day-ahead:2026-06-07
step:site-1:price_import_rdn:2026-06-07
step:site-1:askue:2026-06-06T12:00Z/PT15M
step:site-1:fusionsolar:NE=129469793:2026-06-06T13:00Z/PT1H
step:site-1:site_settings:2026-06-07:input_hash=<hash>
step:site-1:battery_optimization:single-bess-1:2026-06-07:input_hash=<hash>
```

Input hash should include:

- site config version;
- selected instruments;
- horizon;
- price series version;
- prepared site settings version;
- battery asset config;
- current battery state if used;
- operator policy version.

## Retry Rules

Retry policy is per module and data role.

Critical data:

```text
if missing/stale/source_error:
  mark step retrying
  mark dependent workflow blocked
  schedule retry
```

Advisory/informational data:

```text
if missing/stale/source_error:
  mark data degraded or retrying
  continue independent critical workflow
  include warning in advisory report and agent context
```

Source-specific examples:

- FusionSolar station `NE=133657926` returning `failCode=20056` should not stop
  other FusionSolar stations. Mark that station `source_error`; mark the whole
  FusionSolar module `partial` if at least one station succeeded.
- ENTSO-E/RDN price import failure blocks day-ahead optimization for that
  market day.
- ASKUE missing recent load data blocks site optimization only when the selected
  strategy requires load history/forecast; otherwise it degrades the site
  report.
- Deye missing battery SOC blocks operational optimization that depends on
  current SOC.

Fail-closed behavior:

```text
critical data missing -> dependent run blocked
blocking_safety data unsafe -> operational workflow blocked
unknown approval state -> no command
unknown command capability -> no command
unknown site binding -> no orchestration run
```

Fail-open is allowed only for explicitly informational data and must be visible
as a degraded status in the run report.

## Observability And Audit

The orchestrator must follow existing monitoring/audit expectations.

Every run and step log/metric/trace should include:

```text
site_id
run_id
step_id
module
idempotency_key
trigger_type
status
data_role
data_status
attempt_count
reason_code
```

Metrics:

```text
orchestration_run_duration_seconds
orchestration_step_duration_seconds
orchestration_step_attempts_total
orchestration_data_balance_status
orchestration_locks_active
orchestration_blocked_runs_total
```

Audit rules:

- operator-triggered runs are auditable;
- future approval transitions are auditable;
- agent proposals are auditable;
- command activation, when added, must write command/audit records through the
  existing command/audit paths;
- orchestration metadata must explain why a run was blocked, degraded, retried,
  or skipped.

Reason codes should be stable strings, for example:

```text
prices-missing
prices-stale
battery-soc-missing
grid-voltage-unsafe
source-partial
source-error
duplicate-run
lock-held
approval-required
```

## Configuration Model

The orchestrator should be configuration-driven, consistent with the rest of
the project.

Initial config shape:

```text
Orchestration:
  Enabled: true
  SiteId: site-1
  Modules:
    PriceImportRdn:
      Enabled: true
      Schedule: daily
      Criticality: critical
    AskueConsumptionPoll:
      Enabled: true
      PeriodSeconds: 900
      Criticality: critical
    DeyeCloudPoll:
      Enabled: true
      PeriodSeconds: 60
      Criticality: critical
    FusionSolarPoll:
      Enabled: true
      PeriodSeconds: 900
      Criticality: advisory
    AgentProposal:
      Enabled: true
      Mode: advisory
```

Startup validation must reject:

- enabled orchestration without a valid site binding;
- enabled module without required source configuration;
- duplicate module schedule keys;
- command-capable mode without approval policy;
- unknown criticality/status names.

Production default:

```text
orchestration can collect, prepare, optimize and propose;
orchestration cannot dispatch commands until command approval policy exists.
```

## Agent In The Orchestration Loop

The agent is included from the first orchestrator design, but starts
advisory-only.

Agent input must be a bounded context package:

```text
site descriptor
selected instruments
data balance statuses
prepared site settings summary
latest price summary
latest battery optimization result
latest site advisory report
active warnings/safety events
```

Agent input must not include:

```text
API tokens
passwords
cookies
connection strings
private keys
operator auth tokens
```

Agent outputs:

```text
explanation
operator summary
data quality warning
scenario comparison
advisory proposal
future operational proposal
```

Operational proposals require:

```text
schema validation
site/asset binding validation
data readiness validation
instrument validation
grid/safety validation
operator policy validation
explicit approval
audit append
```

No agent proposal may directly dispatch a battery command.

Agent implementation balance:

- first slice may use a deterministic placeholder proposal builder;
- real model calls are a later adapter behind an Application port;
- agent context is built by Application, not by the model adapter;
- model adapter is not allowed to query PostgreSQL directly;
- every proposal stores input context hash and validation result.

## Command Path Future Gate

The orchestrator may eventually initiate control commands, but only through a
separate state transition.

Required states:

```text
advisory_ready
proposal_created
validation_passed
awaiting_approval
approved
command_queued
command_dispatched
command_confirmed
```

Anything before `approved` is read-only.

Safety rule:

```text
grid voltage unsafe -> operational workflow blocked
grid voltage unsafe -> emit safety intent
agent may explain, but cannot override
```

## First Implementation Slice

Build the smallest useful internal orchestrator without executing commands.

Delivered in the first slice:

1. Add orchestration application records:
   - `OrchestrationRun`
   - `OrchestrationStep`
   - `DataBalanceStatus`
   - `OrchestrationLock`
   - trigger/status/data-role enums
2. Add application interfaces:
   - `IOrchestrationRunStore`
   - `IOrchestrationLockStore`
   - `IDataBalanceStore`
   - `IDataReadinessPolicy`
   - `IOrchestrationUseCase`
   - `IOrchestrationModule`
3. Add PostgreSQL migrations for:
   - `orchestration_runs`
   - `orchestration_steps`
   - `orchestration_locks`
   - `orchestration_data_balances`
4. Add Dapper stores.
5. Add in-memory stores and default readiness/use-case behavior.
6. Add DI registration for Application and Persistence.
7. Add focused Application, migration-resource and PostgreSQL roundtrip tests.

Remaining follow-up work:

1. Add first modules as wrappers over existing use cases:
   - RDN price import status check;
   - ASKUE/FusionSolar/Deye data balance update;
   - site settings preparation;
   - battery optimization trigger;
   - agent context/proposal placeholder.
2. Add hosted service scheduler with conservative defaults:
   - RDN daily;
   - ASKUE 15/60 min;
   - Deye frequent;
   - FusionSolar hourly/frequent;
   - site settings after source updates.
3. Add API endpoints:
   - `POST /sites/{siteId}/orchestration/runs/day-ahead`
   - `GET /sites/{siteId}/orchestration/runs/current`
   - `GET /sites/{siteId}/orchestration/data-balances`
4. Add module and API tests:
   - FusionSolar partial success continues;
   - advisory data failure does not block price import;
   - agent proposal is created but does not dispatch command;
   - API status shape is stable.

Implementation order should mirror existing project discipline:

```text
1. Application records and in-memory stores
2. Application unit tests
3. Persistence migration and Dapper stores
4. Persistence tests
5. API/Worker wiring
6. Architecture boundary tests
7. Narrow live/scratch tests only after deterministic tests pass
```

No first slice should introduce:

- a new project split;
- a new database library;
- a new scheduler framework unless the existing hosted service pattern cannot
  support the requirement;
- a direct dependency from Application to adapters;
- command dispatch from agent or optimization output.

## Module Status Semantics

Recommended mapping:

```text
all required sources succeeded -> succeeded
some source succeeded, some failed -> partial
no usable source data and role critical -> blocked
retryable source failure -> retrying
non-retryable validation issue -> failed
module not required by selected instruments -> skipped
```

The orchestrator must distinguish:

```text
module failed
module partial
data unavailable
data stale
data invalid
workflow blocked
workflow degraded but usable
```

This distinction is essential for the agent and operator UI.

## Architecture And Test Gates

Required deterministic tests:

- `OrchestrationUseCaseTests` (first slice delivered)
- `DataReadinessPolicyTests` (covered through first use-case pins; split if policy grows)
- `OrchestrationIdempotencyTests` (covered through first use-case and persistence roundtrip pins)
- `OrchestrationModuleDependencyTests` (follow-up when concrete modules are wired)
- `OrchestrationAgentProposalGateTests` (follow-up)
- `OrchestrationConfigurationValidationTests` (follow-up)
- `OrchestrationPersistenceRoundtripTests` (first slice delivered)
- `DapperOrchestrationRunStoreTests` / `DapperDataBalanceStoreTests` (may stay as focused integration roundtrip unless store complexity grows)

Required regression/boundary gates:

- `BatteryEms.Application.Tests`
- `BatteryEms.Api.Tests`
- `BatteryEms.ArchitectureTests`
- `BatteryEms.Adapters.Persistence.Tests`
- `BatteryEms.Adapters.Optimization.Tests`

Boundary assertions to add or preserve:

```text
Application.Orchestration must not reference driven adapter implementations.
Driven source adapters must not reference Orchestration use cases.
Orchestration must not reference IBatteryCommandSink directly in the first slice.
Agent proposal code must not reference secret-bearing option types.
```

Live tests are opt-in only, following the FusionSolar scratch-test pattern.

## Open Decisions

1. Exact publication-time schedule for Ukrainian RDN import.
2. Whether ASKUE default period should be 15 min or 60 min per site.
3. Whether FusionSolar per-station `source_error` rows are persisted in
   universal site measurements or only in data balance status.
4. How to version site config so input hashes can detect meaningful changes.
5. Whether first agent implementation uses a real model call or a deterministic
   placeholder proposal builder.
6. Exact API response shape for orchestration status UI.

## Success Criteria

The first orchestrator slice is successful when:

- PostgreSQL shows one durable run with ordered steps. Delivered.
- Re-running the same trigger does not duplicate work. Delivered.
- Missing critical data blocks optimization clearly. Delivered at policy/use-case level.
- Advisory data errors degrade without blocking unrelated work. Delivered at policy/use-case level.
- Data-balance status roundtrips through PostgreSQL. Delivered.
- No command is dispatched. Delivered by absence of command-sink dependency.
- Concrete FusionSolar/ASKUE/Deye/RDN modules, site prepared settings trigger,
  battery optimization initiation and agent proposal creation remain follow-up
  work on top of this foundation.
