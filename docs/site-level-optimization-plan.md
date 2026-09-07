# Site-Level Optimization Plan

Branch: `codex/site-level-optimization-layer`

Status: planning and first implementation stage.

## Architectural Alignment

This plan extends the existing BESS-EMS architecture; it does not introduce a
second product architecture.

Normative anchors:

| Rule source | Site-level consequence |
|---|---|
| `spec/architecture.md` dependency rule | Site logic lives in `BatteryEms.Application.Site`; adapters only collect/persist facts through Application ports. |
| ADR 0011 Application monolithic module | Add namespaces inside `BatteryEms.Application`; do not create a new Application `csproj` for Site or Orchestration. |
| ADR 0001 persistence | Runtime storage uses PostgreSQL through Dapper/Npgsql stores and forward-only DbUp migrations. No EF Core or parallel migration stack. |
| ADR 0007 multi-asset worker | One deployment may coordinate multiple sites/assets. Failures must be isolated by `site_id` and `asset_id`, not allowed to stop unrelated work. |
| ADR 0008 edge boundary | Site EMS remains supervisory/advisory unless a later approved safety/control slice exists. It does not claim hard real-time or replace BMS/PCS/hardware protection. |
| `data_atribute.md` | ASKUE, Deye, FusionSolar, ENTSO-E/RDN and future sources normalize into the same site measurement vocabulary. |
| `docs/orchestrator-plan.md` | The orchestrator decides when modules run and whether data is ready; Site logic decides how prepared site facts affect advisory optimization. |

Layering rule:

```text
source adapters collect raw facts
site settings prepares canonical site facts
orchestration schedules and gates work
site advisory evaluates site-level intent
battery optimizer remains responsible for battery schedules
control/safety remains responsible for executable commands
```

`site_id` is the coordination key. `asset_id` is the battery calculation and
command-ownership key. A source-native station, meter or domain id is only an
`instrument_id`; it must not be promoted into either `site_id` or `asset_id`.

## Core Rule

The existing battery calculation engine must remain stable and isolated.

Do not change these battery-core paths as part of the first site-level
optimization stage:

- `ControlCycleUseCase`
- `BatteryTelemetry`
- `ISnapshotStore`
- `ScheduleOptimizationRequest`
- OR-Tools battery optimization model
- battery command dispatch path

Site-level optimization is added as a separate upper layer that may call the
existing battery optimizer, but must not rewrite it.

The site-level architecture must support multiple sites. A single-site setup is
only the first deployment profile, not a permanent model assumption.

## Initial EMS Logic

The original EMS was designed as a battery energy management system. Its job is
to operate a BESS asset safely and economically.

Primary inputs:

- battery SOC/SOH
- current battery active power
- battery capacity and power limits
- min/max SOC limits
- charge/discharge efficiency
- market price series
- active schedules and reserve commitments
- operator stop/safety state

Primary outputs:

- battery schedule windows
- battery target power
- optimization run metadata
- economics report: cost, revenue, net profit, losses, degradation cost

Original strategy:

- charge when energy is cheap
- discharge when energy is expensive
- keep SOC inside configured limits
- respect charge/discharge power limits
- account for energy losses
- account for battery degradation
- fall back safely when telemetry or schedules are not usable

This is a battery-only optimization model. It was not originally a complete
site energy management model.

## Why Site-Level Optimization Is Separate

PV, load, and grid telemetry are site-level signals. They have different data
quality and semantics than battery telemetry.

Examples:

- Deye `/station/latest` returns site metrics at root JSON level and may return
  cached values.
- FusionSolar `/getKpiStationHour` returns hourly KPI records. Its
  `inverterYield` is energy over an hour, not instant power.
- Some fields can be missing, and missing values must stay `null`, not be
  coerced to zero.

If those signals are injected directly into the battery optimizer too early,
the optimizer can make wrong decisions based on stale, incomplete, or
misinterpreted site data.

Therefore the first site-level implementation must be read-only/advisory.

## Target Architecture

```text
PV / load / grid telemetry
RDN / DAM prices
battery state
asset limits
site constraints
        |
        v
Site Optimization Layer
        |
        v
Battery optimization request
        |
        v
Existing battery optimizer
        |
        v
Battery schedule / advisory plan
```

The site layer decides which site-aware objective or constraints should be
tested. The existing battery optimizer still solves the battery schedule.

## Multi-Site Architecture

The site layer must treat `site_id` as a first-class key.

One EMS deployment may manage:

- one site with one BESS asset
- one site with multiple BESS assets
- multiple sites, each with its own BESS/PV/load/grid telemetry
- multiple PV stations feeding one logical site
- one site with multiple grid connections
- one PV provider account containing stations for several sites

Proposed logical model:

```text
Site
  site_id
  name
  timezone
  market_bid_area
  grid_connections[]
  asset_ids[]
  pv_source_refs[]
  load_source_refs[]
  grid_meter_source_refs[]
  available_instruments[]
```

The existing battery `asset_id` remains the identifier for a battery asset.
The new `site_id` groups one or more assets and external telemetry sources.

```text
site_id
  ├── battery asset_id A
  ├── battery asset_id B
  ├── FusionSolar stationCode X
  ├── Deye stationId Y
  ├── grid connection G1
  ├── grid connection G2
  └── grid/load meter Z
```

The site optimizer should operate by `site_id`, then delegate battery-only
schedule generation to the existing battery optimizer per `asset_id`.

Important invariant:

```text
site_id is for coordination
asset_id is for battery calculation and command ownership
```

No site-level code may blur those two responsibilities.

## Grid Connections

Every site must have at least one grid connection. The number of grid
connections can differ by site.

Grid connection is a physical/electrical constraint, not only telemetry.

Proposed model:

```text
GridConnection
  grid_connection_id
  name
  planned_voltage_v
  voltage_tolerance_percent
  min_voltage_v
  max_voltage_v
  max_phase_imbalance_percent
  max_import_power_kw
  max_export_power_kw
  export_allowed
  enabled
  meter_source_refs[]
  notes
```

Rules:

```text
site must contain grid_connections[1..n]
planned_voltage_v must be positive
voltage limits must be configured by the application
min_voltage_v must be less than planned_voltage_v
max_voltage_v must be greater than planned_voltage_v
max_phase_imbalance_percent must be >= 0
max_import_power_kw must be positive
max_export_power_kw must be >= 0
enabled is required
if enabled=false, effective max_import_power_kw=0
if enabled=false, effective max_export_power_kw=0
if export_allowed=false, effective max_export_power_kw=0
if export_allowed=true, max_export_power_kw must be > 0
```

For site-level optimization, each grid connection can have its own import and
export limit. The site report should show both:

```text
per-grid-connection import/export
per-grid-connection voltage health
total site import/export
```

This matters because two sites can have the same battery/PV capacity but very
different network constraints.

Example:

```text
site_id=site-a
grid_connections:
  - grid_connection_id=main-10kv
    planned_voltage_v=10000
    min_voltage_v=9000
    max_voltage_v=11000
    max_phase_imbalance_percent=5
    max_import_power_kw=1000
    max_export_power_kw=500
    export_allowed=true
    enabled=true

site_id=site-b
grid_connections:
  - grid_connection_id=main-0_4kv
    planned_voltage_v=400
    min_voltage_v=360
    max_voltage_v=440
    max_phase_imbalance_percent=5
    max_import_power_kw=150
    max_export_power_kw=0
    export_allowed=false
    enabled=true
```

The optimizer must validate grid constraints before advisory calculation. A
site without grid connection data is not valid for site-level optimization.

If a grid connection is disabled, it remains part of the site model for
reporting and audit, but it cannot carry import or export power in the
optimization.

If all grid connections are disabled, the site is electrically islanded from
the external grid. The first advisory implementation should reject this state
unless an explicit future island-mode instrument exists.

## Three-Phase Voltage Health

Each grid connection is three-phase. The system must track planned voltage and
actual voltage for all three phases.

Telemetry shape:

```text
GridVoltageTelemetry
  site_id
  grid_connection_id
  timestamp
  phase_l1_voltage_v
  phase_l2_voltage_v
  phase_l3_voltage_v
  data_quality
```

The application owns allowed voltage limits. These limits can be configured
directly as `min_voltage_v`/`max_voltage_v` or derived from
`planned_voltage_v` plus `voltage_tolerance_percent`.

Voltage health checks:

```text
L1, L2, L3 must be finite
L1, L2, L3 must be within [min_voltage_v, max_voltage_v]
phase imbalance must be <= max_phase_imbalance_percent
```

Recommended phase imbalance calculation:

```text
average = (L1 + L2 + L3) / 3
max_deviation = max(abs(L1-average), abs(L2-average), abs(L3-average))
imbalance_percent = max_deviation / average * 100
```

If any phase leaves the configured voltage range, or the phase imbalance
exceeds the configured limit, the system must create an explicit island
transition intent.

Important:

```text
voltage safety is not an optimizer preference
voltage safety is a protection/safety trigger
```

The site optimizer may observe grid health, but it must not silently decide
whether to island. Island transition is a separate safety action.

Proposed safety output:

```text
GridSafetyEvent
  event_id
  site_id
  grid_connection_id
  timestamp
  reason
  measured_l1_voltage_v
  measured_l2_voltage_v
  measured_l3_voltage_v
  min_voltage_v
  max_voltage_v
  phase_imbalance_percent
  requested_action = transition_to_island
```

First implementation rule:

```text
if grid voltage is out of range -> emit transition_to_island intent
if all enabled grid connections are unhealthy -> site is grid-unsafe
```

The actual breaker/inverter command path for island transition must be designed
as a separate safety command adapter. It should not be mixed into advisory
optimization.

## Agent Layer

The site-level architecture should include a place for an agent, but the agent
must not become an uncontrolled command path.

The agent is a reasoning and orchestration layer above site telemetry,
forecasts, advisory optimization, operator policy, and safety events.

Initial agent role:

```text
observe
explain
compare scenarios
recommend
request approval
audit decisions
```

The agent must not directly:

```text
write battery commands
change optimizer math
override voltage/grid safety
trigger island transition without safety validation
change site configuration without audit
hide missing or stale data quality
```

Recommended architecture:

```text
Telemetry / Prices / Forecasts / Site Config
        |
        v
Site Advisory Optimizer
        |
        v
Agent Reasoning Layer
        |
        v
Operator Approval / Policy Gate
        |
        v
Existing EMS Use Cases / Safety Use Cases
        |
        v
Battery schedule / site safety intent / audit record
```

Agent capabilities should be represented explicitly:

```text
AgentCapability
  explain_site_state
  compare_battery_only_vs_site_plan
  propose_site_advisory_plan
  propose_instrument_selection
  propose_config_change
  propose_island_transition_review
  generate_operator_summary
```

The agent can propose. The application decides whether a proposal is accepted.

## Agent Proposal Model

Agent output should be structured and auditable.

```text
AgentProposal
  proposal_id
  site_id
  created_at
  proposal_type
  confidence
  summary
  rationale
  required_inputs
  data_quality_warnings[]
  referenced_run_ids[]
  proposed_action
  approval_required
  expires_at
```

Initial proposal types:

```text
site_advisory_plan
instrument_selection
config_change
operator_warning
island_transition_review
```

The first implementation should store agent proposals but not execute them.

## Agent Approval Gates

Agent proposals must pass through deterministic gates before they affect the
EMS.

Gate order:

```text
1. schema validation
2. site_id and asset_id binding validation
3. data quality validation
4. instrument availability validation
5. grid/safety validation
6. operator policy validation
7. explicit approval when required
8. audit append
```

For early stages, all agent proposals that could change operation require
operator approval.

Examples:

```text
explain_site_state -> no approval, read-only
compare scenarios -> no approval, read-only
propose advisory plan -> approval optional, no automatic dispatch
propose schedule activation -> approval required
propose config change -> approval required
propose island transition -> safety validation + approval path
```

Safety-critical events are different from economic optimization. If voltage is
out of range, the deterministic safety logic emits the island transition intent.
The agent may explain the event and prepare an operator summary, but it must
not be the only source of the safety trigger.

## Agent Context Contract

The agent should receive a bounded context package, not direct database access.

```text
AgentContext
  site_descriptor
  selected_instruments
  latest_site_telemetry
  grid_voltage_health
  price_series_summary
  battery_status_summary
  latest_battery_schedule
  latest_site_advisory_report
  active_safety_events
  data_quality_summary
```

No secrets should be included in agent context.

Forbidden agent context fields:

```text
API tokens
passwords
raw cloud credentials
connection strings
private keys
operator auth tokens
```

## Agent Audit Requirements

Every agent interaction that produces a proposal should be persisted.

```text
AgentAuditRecord
  proposal_id
  site_id
  created_at
  model_or_agent_version
  input_context_hash
  proposal_json
  validation_result
  approval_state
  approved_by
  approved_at
  final_outcome
```

Audit must preserve enough information to answer:

```text
what did the agent see?
what did it recommend?
why did it recommend it?
who approved it?
what did the EMS actually do?
```

## Agent First Implementation Slice

The first agent slice should be advisory only.

Steps:

1. Add `AgentProposal` and `AgentAuditRecord` records.
2. Add `IAgentProposalStore`.
3. Add deterministic `AgentProposalValidator`.
4. Add endpoint:
   - `POST /sites/{siteId}/agent/proposals`
   - creates/stores an agent proposal
   - validates it
   - does not execute it
5. Add endpoint:
   - `GET /sites/{siteId}/agent/proposals/current`
   - lists current proposals for operator review
6. Add tests proving:
   - secrets are not included in agent context
   - invalid site/instrument proposals are rejected
   - operational proposals require approval
   - read-only explanation proposals do not dispatch commands
   - safety-trigger proposals cannot bypass voltage validation

## Site Instruments

Each site must explicitly declare which optimization instruments are available.
The optimizer must not assume that every energy source is controllable.

Example:

```text
site_id=site-kyiv-1
available_instruments:
  - battery_arbitrage
  - pv_self_consumption
  - grid_import_limit
  - grid_export_limit
```

An instrument is an action family that the site optimizer may use. A source is
only data unless an instrument says it is controllable.

Examples:

```text
PV telemetry source        -> data only
PV curtailment instrument  -> controllable PV output
BESS telemetry source      -> data
BESS battery_arbitrage     -> controllable battery schedule
load meter                 -> data only
load_shift instrument      -> controllable flexible load
grid meter                 -> data only
grid_import_limit          -> optimization constraint
```

Initial instrument catalog:

```text
battery_arbitrage
  Uses existing battery optimizer to charge/discharge against prices.

battery_self_consumption
  Uses battery to absorb PV surplus and reduce grid import/export.

pv_self_consumption
  Treats PV as available generation for site balance, but not controllable.

pv_curtailment
  Allows optimizer to reduce PV export when curtailment control exists.

grid_import_limit
  Keeps modeled grid import under configured grid connection limits.

grid_export_limit
  Keeps modeled grid export under configured grid connection limits.

load_shift
  Allows optimizer to move flexible load inside allowed windows.

dispatchable_generator
  Allows optimizer to dispatch diesel/gas/CHP generation if configured.

market_export
  Allows site-level strategy to value export separately from import.
```

First-stage support should be limited to reporting/advisory for:

```text
battery_arbitrage
pv_self_consumption
grid_import_limit
grid_export_limit
market_export
```

Later stages can add true control authority for:

```text
pv_curtailment
load_shift
dispatchable_generator
battery_self_consumption
```

## Instrument Selection Rules

Instrument selection is per site, not global.

```text
site A may use: battery_arbitrage, pv_self_consumption
site B may use: battery_arbitrage, pv_curtailment, load_shift
site C may use: grid_import_limit only
```

The site optimizer must validate that each selected instrument has the required
source bindings and configuration.

Examples:

```text
battery_arbitrage requires at least one battery asset_id
pv_self_consumption requires at least one PV source binding
pv_curtailment requires a PV control adapter, not only PV telemetry
grid_import_limit requires at least one grid connection with max_import_power_kw
grid_export_limit requires grid connections with export_allowed/max_export_power_kw
grid safety requires L1/L2/L3 voltage telemetry and configured voltage limits
load_shift requires flexible load windows and max shift energy
dispatchable_generator requires generator capacity, fuel cost, and ramp limits
```

If an instrument is missing required data, the site optimizer should reject the
advisory request with a clear validation error rather than silently assuming
defaults.

## Current Building Blocks

Already implemented:

- `SiteTelemetry`
- `ISiteTelemetryStore`
- `InMemorySiteTelemetryStore`
- `GET /site/{assetId}/status` as a transitional read model for battery-bound
  site telemetry; future site operations use `/sites/{siteId}/...`
- Deye site telemetry extraction into `ISiteTelemetryStore`
- FusionSolar site telemetry adapter into `ISiteTelemetryStore`
- RDN/ENTSO-E price import and persistence
- battery optimization economics report
- nonlinear degradation and loss reporting

## New Module Shape

Proposed namespace:

```text
BatteryEms.Application.Site
```

Per ADR 0011 this is a namespace inside the existing Application project, not a
new Application module/project. Persistence implementations belong to
`BatteryEms.Adapters.Persistence`; source protocol details belong to their
driven adapters.

Initial interfaces:

```text
ISiteOptimizationUseCase
ISiteForecastSource
ISiteConstraintProvider
ISiteOptimizationReportRepository
IAgentProposalStore
```

Initial records:

```text
SiteDescriptor
SiteAssetBinding
SiteTelemetryBinding
GridConnection
GridVoltageTelemetry
GridSafetyEvent
SiteInstrument
SiteInstrumentSet
AgentProposal
AgentAuditRecord
SiteOptimizationCommand
SiteOptimizationResult
SiteOptimizationPlan
SiteOptimizationStep
SitePowerBalance
SiteOptimizationObjective
```

The first version should be advisory:

```text
input: site id, horizon, time step, price source/reference
output: site-aware advisory plan and comparison with battery-only plan
side effect: no automatic battery command dispatch
```

The command should allow an optional subset of `asset_ids`, but the default is
to include all batteries bound to the site.

## Data Model

Per time step, the site layer should reason about:

```text
site_id
timestamp
pv_power_kw or pv_energy_kwh
load_power_kw or load_energy_kwh
grid_connection_id
phase_l1_voltage_v
phase_l2_voltage_v
phase_l3_voltage_v
grid_import_kw
grid_export_kw
battery_asset_id
battery_charge_kw
battery_discharge_kw
battery_soc_percent
market_price
losses_kwh
degradation_cost
site_net_profit
```

Important distinction:

- Deye current telemetry can represent near-real-time site power.
- FusionSolar hourly `inverterYield` represents hourly PV energy. For a
  one-hour interval it can be represented as average `pv_power_kw`, but the
  report must keep the source semantics clear.

For multi-site data, all telemetry must be normalized into a common shape:

```text
site_id
source_type
source_id
timestamp
interval_start
interval_end
metric
value
unit
data_quality
```

The first in-memory implementation may keep the simpler `SiteTelemetry` shape,
but the persistence model should be designed around `site_id` and `source_id`.
For new persisted source data, prefer the `SiteMeasurementReading` vocabulary
from `data_atribute.md`; specialized tables such as `price_series` may remain
canonical when they already model a domain more precisely.

## First Implementation Slice

Goal: produce a site-aware advisory report without changing battery operation.

Steps:

1. Add `BatteryEms.Application.Site` records and use-case interface.
2. Add `ISiteRegistry` with in-memory implementation.
3. Add `SiteDescriptor` config/loading path for one or more sites, including
   required `grid_connections`.
4. Add site instrument catalog and validation.
5. Add an in-memory site optimization report repository.
6. Add in-memory agent proposal/audit store.
7. Implement a first `DefaultSiteOptimizationUseCase` that:
   - loads site configuration by `site_id`
   - validates selected instruments for the site
   - validates grid connection limits and export permission
   - evaluates three-phase grid voltage health
   - resolves battery asset configuration for all bound `asset_ids`
   - loads price series
   - reads current site telemetry snapshots for sources bound to the site
   - calls existing battery schedule optimization use case or optimizer per
     battery asset
   - builds a site-level report around produced battery schedules
8. Add API endpoint:
   - `POST /sites/{siteId}/optimize/advisory`
   - returns advisory plan only
9. Add advisory-only agent proposal endpoints:
   - `POST /sites/{siteId}/agent/proposals`
   - `GET /sites/{siteId}/agent/proposals/current`
10. Add tests proving:
   - battery optimizer request shape is unchanged
   - site report can be produced with PV/load data
   - multiple sites do not mix telemetry or schedules
   - one site can bind multiple source ids
   - selected instruments are validated against site capabilities
   - unavailable instruments are rejected
   - missing PV/load/grid fields are tolerated
   - no battery command is dispatched

## First Advisory Strategy

Version 1 should not try to solve full co-optimization.

It should:

- run or reuse the existing battery-only schedule
- overlay PV/load/grid telemetry on top of that schedule
- calculate site power balance
- calculate estimated grid import/export
- calculate market revenue/cost
- compare battery-only economics with site-aware economics

This gives visibility before control authority.

## Later Optimization Strategy

After advisory reports are validated, the site layer can start creating
site-aware battery objectives.

Possible future goals:

- reduce grid import during expensive hours
- absorb PV surplus instead of curtailing/exporting cheaply
- avoid discharge when PV already covers load
- avoid charge from grid when PV surplus is expected soon
- respect grid import/export caps
- maximize net site profit, not only battery arbitrage

This should still be done by translating site intent into battery optimizer
inputs or constraints, not by rewriting the battery engine.

## API Plan

Initial endpoint:

```text
POST /sites/{siteId}/optimize/advisory
```

Request:

```json
{
  "asset_ids": null,
  "instruments": [
    "battery_arbitrage",
    "pv_self_consumption",
    "grid_import_limit",
    "grid_export_limit"
  ],
  "horizon_start": "2026-06-05T00:00:00+03:00",
  "horizon_end": "2026-06-06T00:00:00+03:00",
  "time_step_seconds": 3600,
  "price_series": {
    "market_bid_area": "10Y1001A1001A869",
    "product": "day_ahead",
    "price_kind": "rdn",
    "source": "entso-e"
  }
}
```

Response:

```json
{
  "site_id": "site-kyiv-1",
  "mode": "advisory",
  "instruments": [
    "battery_arbitrage",
    "pv_self_consumption",
    "grid_import_limit",
    "grid_export_limit"
  ],
  "battery_schedules": [
    {
      "asset_id": "single-bess-1",
      "schedule_version": 12
    }
  ],
  "site_summary": {
    "pv_energy_kwh": 0,
    "load_energy_kwh": 0,
    "grid_import_kwh": 0,
    "grid_export_kwh": 0,
    "net_profit": 0
  },
  "grid_connections": [
    {
      "grid_connection_id": "main-10kv",
      "planned_voltage_v": 10000,
      "phase_l1_voltage_v": 10020,
      "phase_l2_voltage_v": 10010,
      "phase_l3_voltage_v": 9990,
      "enabled": true,
      "export_allowed": true,
      "max_import_power_kw": 1000,
      "max_export_power_kw": 500
    }
  ],
  "steps": []
}
```

## Safety Invariants

The first stage must guarantee:

- no direct write to battery command sink
- no direct change to control-cycle behavior
- no change to battery optimizer math
- no change to `BatteryTelemetry`
- site-level operations are keyed by `site_id`
- battery control operations remain keyed by `asset_id`
- every site has at least one valid grid connection
- disabled grid connections have zero effective import/export capacity
- at least one grid connection must be enabled unless island mode is explicitly supported
- three-phase voltage must be monitored per grid connection
- out-of-range phase voltage triggers transition-to-island intent
- phase imbalance above configured limit triggers transition-to-island intent
- agent proposals cannot bypass deterministic validation gates
- agent context must not contain secrets
- operational agent proposals require approval before execution
- export is forbidden when `export_allowed=false`
- grid import/export must respect per-connection limits
- telemetry from one site cannot be used in another site's advisory report
- selected instruments must be explicit and validated
- telemetry sources do not imply control authority
- unavailable control instruments must fail closed
- all site data quality is explicit
- missing site telemetry does not block battery-only operation
- site optimization failures do not put EMS into unsafe state

## Test Plan

Required focused tests:

- `SiteOptimizationUseCaseTests`
- `SiteRegistryTests`
- `SiteInstrumentValidationTests`
- `SiteOptimizationEndpointTests`
- `SitePowerBalanceCalculatorTests`
- `AgentProposalValidatorTests`
- `AgentProposalEndpointTests`
- `FusionSolarSiteTelemetrySourceTests`
- `DeyeCloudTelemetrySourceTests`

Multi-site focused cases:

- two sites with different PV snapshots produce different reports
- unknown `site_id` returns 404
- source binding prevents station data from leaking across sites
- one site with two battery assets produces two battery schedule references
- site without grid connection is rejected
- site with all grid connections disabled is rejected until island mode exists
- disabled grid connection contributes zero import/export capacity
- out-of-range L1/L2/L3 voltage emits transition-to-island intent
- excessive phase imbalance emits transition-to-island intent
- export is zero when `export_allowed=false`
- multiple grid connections are reported separately and aggregated at site level
- site with PV telemetry but no `pv_curtailment` instrument cannot curtail PV
- requesting `load_shift` without flexible load config returns validation error

Required regression tests:

- `BatteryEms.Application.Tests`
- `BatteryEms.Api.Tests`
- `BatteryEms.ArchitectureTests`
- `BatteryEms.Adapters.Optimization.Tests` using `.\.dotnet-x64\dotnet.exe`

Success condition:

```text
site-level advisory report exists
battery engine tests remain green
optimizer tests remain green
architecture tests remain green
```

## Recommended Next Step

Start with a pure reporting slice:

```text
current PV/load/grid snapshot
+ RDN price horizon
+ existing battery schedule
= site advisory report
```

Only after the report makes physical and economic sense should the site layer be
allowed to influence the battery schedule request.

For multi-site readiness, the first reporting slice should still use
`site_id` in the API and internal command, even if the initial config contains
only one site.
