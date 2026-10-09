# ADR 0013: Agent-controlled Deye Cloud BESS planning and guarded activation

- Status: Proposed
- Date: 2026-09-02
- Decision owners: EMS maintainers and site operator
- Scope: one configured Deye Cloud station first, multi-site compatible design
- Safety posture: read-only by default; no production write control in this ADR

## Context

The requested capability is a daily, chat-driven workflow that:

1. reports PV generation, energy charged into the battery and energy discharged;
2. obtains the next day's Ukrainian day-ahead prices;
3. uses the existing EMS optimizer to produce the economically best battery plan;
4. explains and previews that plan in chat; and
5. after deterministic validation and explicit approval, activates the plan through
   Deye Cloud.

This is supervisory control, not a hard-real-time control loop. Deye Cloud is an
Internet service with asynchronous commands. BMS, inverter firmware and the local
EMS safety loop remain authoritative for electrical protection.

The public vendor sources used for this review are the
[DeyeCloud developer portal](https://developer.deyecloud.com/) and the
[official Deye OpenAPI/MCP tool reference](https://developer.deyecloud.com/openmcp/docs/deye-open-mcp-tools.html).
For Ukrainian prices, the reviewed primary sources are the
[Market Operator hourly price page](https://www.oree.com.ua/index.php/pricectr/?lang=ukr),
the [ENTSO-E data-extraction guide](https://transparency.entsoe.eu/content/static_content/download?path=%2FStatic+content%2Fweb+api%2FIG-for-TP-data-extraction-process.pdf),
and ENTSO-E's published area-code reference, which identifies `10Y1001C--00003F`
as the Ukraine bidding zone.

## Decision

Add a guarded application workflow above the existing optimization and safety
use cases. The chat agent may observe, explain and propose. It must never receive
raw Deye credentials and must never call arbitrary Deye endpoints. Mutating chat
operations are limited to creating an activation proposal. A deterministic policy
gate, explicit approval and a dedicated command adapter own execution.

For the first production-capable slice, use Deye Cloud only to install a complete
day-ahead Time-of-Use (TOU) schedule and verify its asynchronous result. Do not use
cloud calls as an hourly or 1 Hz setpoint channel. Do not expose custom Modbus,
battery-type, firmware, work-mode, export-mode or power-limit commands to the agent.

The selected control path is:

```text
chat request
  -> bounded read model
  -> EMS optimizer
  -> Deye TOU projection (maximum six ordered periods)
  -> deterministic preview and policy validation
  -> explicit approval with expiry
  -> durable outbox
  -> Deye TOU update
  -> order-result polling
  -> read-back of TOU configuration
  -> audit + operator result
```

No command is dispatched merely because an LLM recommended it.

## Evidence from the repository

### Current committed baseline (`ac08e91`)

| Capability | Present | Evidence / limitation |
|---|---:|---|
| Battery domain and telemetry | Yes | `BatteryTelemetry`, `BatteryAsset`, SOC/power/temperature limits |
| Day-ahead schedule optimization | Yes | `DefaultScheduleOptimizationUseCase` and OR-Tools GLOP optimizer |
| Economics objective | Partial | Energy cost, optional linear degradation and SOC-target penalty |
| Safety clamps | Yes | `ConstraintLimiter`, `RampLimiter`, `AdapterWriteLimiter` |
| Schedule/run persistence | Yes | PostgreSQL/Dapper schedules, commands, optimization runs, telemetry |
| Price-series port/import | Yes | `PriceSeries`, `IPriceSeriesSource`, import endpoint and exact lookup |
| External Ukrainian price adapter | No | Only a design plan exists in committed `HEAD` |
| Deye telemetry adapter | No | Not present in committed `HEAD` |
| Deye command adapter | No | `IBatteryCommandSink` has no Deye implementation |
| Agent proposal/approval workflow | No | No bounded chat tools, proposal state machine or approval token |

The current optimizer has two blockers for Ukrainian production use:

- it hard-rejects every price unit except `EUR/MWh`, while Ukrainian DAM prices
  need a currency-correct `UAH/MWh` path;
- initial SOC is a singleton solver option and, when absent, falls back to the
  midpoint of the permitted SOC range instead of the latest measured SOC.

Both must be corrected before a plan can be considered executable.

### Uncommitted work in the primary checkout

The primary checkout at `C:\Users\admin\temp\bess-ems` contains extensive
uncommitted work, including Deye Cloud, ENTSO-E, site-level telemetry,
orchestration and persistence files. Those changes were inspected read-only and
were not copied, modified or overwritten by this worktree.

The Deye implementation there is useful as a prototype, but is telemetry-only.
It currently:

- authenticates with `/account/token`;
- polls `/station/latest` or `/device/latest`;
- maps station PV/load/grid values into site telemetry;
- has no historical-energy collector and no command sink;
- stamps samples with local `UtcNow` instead of vendor `lastUpdateTime`, which can
  make cached/stale cloud data look fresh;
- substitutes SOH `100%`, temperature `25 C`, reactive power `0` and fault `OK`
  when Deye did not provide them;
- may mark telemetry valid even when required battery metrics were not parsed;
- models `/device/latest` as `data`, while the current official response uses
  root `deviceDataList`;
- does not refresh/re-authenticate specifically on 401, honor `Retry-After`, use
  jitter, or expose a circuit-breaker state.

These are production blockers, not cosmetic issues.

## Read-only validation against the configured account

On 2026-09-02, the supplied credentials and station were exercised only through
the allowlisted read endpoints in `tools/audit-deye-readonly.ps1`. Secrets,
tokens, station IDs, serial numbers and telemetry values were excluded from output.

| Check | Result |
|---|---|
| `/account/token` | Success, API code `1000000` |
| `/station/latest` | Success; generation, consumption, grid, charge, discharge, battery power, SOC and vendor update time are present |
| `/station/device` | Success; 4 devices, including inverter and collector types |
| `/device/measurePoints` | Success; 144 supported points |
| Energy points on inverter | `PVDailyPowerGenerationActive`, `TotalChargeEnergy`, `TotalDischargeEnergy` are available |
| `/device/latest` | Success; 120 current metrics and a collection timestamp |
| `/config/battery` | Success; capacity, low/shutdown SOC and current limits are readable |
| `/config/system` | Success; energy pattern, work mode, sell/solar/zero-export limits are readable |
| `/config/tou` | Success; TOU activation and period configuration are readable |
| `/station/history` probe | API code `2101006`; not usable with the tested daily request shape |
| `/station/historyPower` probe | Not usable with the tested request/path shape |

Therefore the configured account demonstrably has the observations needed for a
daily report, but write permissions were deliberately not tested. Daily charged
and discharged energy should initially be calculated from persisted, timestamped
deltas of the cumulative `TotalChargeEnergy` and `TotalDischargeEnergy` counters.
PV daily energy can use `PVDailyPowerGenerationActive`, with interval integration
as a reconciliation check. Counter reset, rollover and device replacement must
produce explicit data-quality events, never a negative daily value.

The history failures may be caused by request semantics, endpoint version or
account/product permissions. They do not block the counter-delta approach, but the
vendor contract/OpenAPI application configuration should be checked before relying
on historical backfill.

## Deye API capability matrix

The official reference exposes account, station, device, configuration, control
and dynamic-strategy families. The table groups endpoints by operational meaning;
it does not grant them to the agent.

| Endpoint / capability | R/W | Verified for configured station | Risk | Integration decision |
|---|---|---:|---|---|
| `account/token`, account info | Read/auth | Token verified | Medium: credential leakage | Credential vault + private client only |
| station list/details/devices/latest | Read | latest + devices verified | Low | Discovery and site read model |
| station alerts | Read | Not tested | Low | Later health enrichment |
| station history/history power | Read | Probe unsuccessful | Low | Do not depend on it initially |
| device list/latest/measure points | Read | Verified | Low | Canonical metric discovery and polling |
| device history/history raw | Read | Not tested | Low | Preferred backfill after schema/permission test |
| device alerts | Read | Not tested | Low | Later fault reconciliation |
| logger register/add/delete | Write | Not tested | High | Excluded; asset administration only |
| station create/update/delete | Write | Not tested | Critical | Permanently excluded from agent tools |
| `config/battery` | Read | Verified | Low | Precondition snapshot only |
| `config/system` | Read | Verified | Low | Precondition snapshot only |
| `config/tou` | Read | Verified | Low | Preview base and post-write verification |
| order result | Read | No order created | Low | Required after an approved write |
| battery-mode enable/disable | Write | Not tested | High | Excluded from first slice |
| battery parameter/type update | Write | Not tested | Critical | Excluded from agent tools |
| custom Modbus control | Write | Not tested | Critical | Permanently excluded from chat/LLM path |
| peak-shaving / smart-load control | Write | Not tested | High | Separate future instrument and ADR |
| energy pattern / limit mode / work mode | Write | Not tested | High | Manual commissioning, not daily planning |
| max sell/solar power, solar sell | Write | Not tested | Critical | Separate operator-only workflow |
| TOU update and TOU switch | Write | Not tested | High | Only first-slice allowlisted write family |
| dynamic-control write | Write | Not tested | Critical: changes several settings atomically | Do not use in first production slice |
| dynamic-control read/read-result | Read/async | Not tested | Medium | Optional capability discovery only |

The public documentation does not state a dependable numeric rate limit. The
adapter must therefore honor `Retry-After`, use exponential backoff with jitter,
limit concurrency per account, cache discovery/configuration, and treat a rate
limit as a first-class source status. A successful short probe is not evidence of
production request entitlement.

## Price-source audit

The uncommitted ENTSO-E adapter follows the correct architectural port and sends
an A44 day-ahead price request, but it is not ready for Ukrainian production:

1. configured domain `10Y1001C--000182` is UA-IPS/control-area oriented; the
   canonical Ukraine bidding zone for price queries is `10Y1001C--00003F`;
2. both next-day and prior-day read-only probes returned HTTP `503`, including a
   probe with the corrected bidding-zone code;
3. the adapter has no retry/backoff/cache/source-health state;
4. it assumes an hourly step and exact full coverage, which is good for safety but
   needs explicit 23/24/25-hour local-day handling;
5. the optimizer rejects the likely `UAH/MWh` result because it is hard-coded to
   `EUR/MWh`;
6. degradation and SOC penalty parameters are also named and reported in EUR, so
   merely accepting a UAH price label would mix currencies in one objective.

Decision for prices:

- use ENTSO-E A44 with Ukraine BZN `10Y1001C--00003F` as the automated primary
  only after reliable responses and terms are confirmed;
- use the official Market Operator export as reconciliation/fallback via a
  supported file/API workflow, not fragile HTML scraping;
- persist source, retrieval time, publication version/hash, currency, timezone,
  resolution and completeness;
- keep all internal timestamps UTC, but define the delivery day in
  `Europe/Kyiv`; generate 23, 24 or 25 steps on DST transitions rather than
  forcing 24;
- generalize optimization currency to `Currency/MWh` and configure degradation
  and penalty costs in the same currency. Never silently convert EUR to UAH;
- block planning when the next-day series is incomplete, stale, duplicated or in
  a different currency from other objective components.

## Target components

```text
DeyeCloudReadClient                ENTSO-E / OREE source adapters
  | latest/config/counters           | versioned price series
  v                                  v
Telemetry + Energy Ledger         PriceSeries store + source health
  \                                  /
   -> DailyPlanningOrchestrator <-
          | latest real SOC
          v
      existing EMS optimizer
          |
          v
      Plan + economics + constraints
          |
          v
      DeyeTouProjector (<= 6 periods)
          |
          v
      AgentProposal (advisory)
          |
          v
 DeterministicPolicyGate -> Approval -> DurableOutbox
                                      -> DeyeCloudCommandAdapter
                                      -> order poll + config read-back
                                      -> Audit ledger
```

### Application ports

Add ports without leaking vendor DTOs into Application:

- `IDeviceEnergySource`: latest counters and source timestamp;
- `IDeviceConfigurationReader`: battery/system/TOU snapshots;
- `IPriceSeriesSource`: retain existing port, add source-health/version metadata;
- `IDailyPlanningUseCase`: builds and stores a dry-run plan;
- `IControlPlanProjector`: converts an EMS schedule into a vendor-neutral control plan;
- `IActivationProposalStore` and `IApprovalStore`;
- `IControlPlanOutbox`;
- `IBatteryScheduleCommandSink`: sends a complete schedule, distinct from the
  1 Hz `IBatteryCommandSink` setpoint port;
- `ICommandVerificationSource`: asynchronous order status and read-back.

### Daily energy ledger

Persist immutable samples and derived intervals:

```text
device_energy_samples
  site_id, asset_id, source_device_ref
  provider_timestamp_utc, received_at_utc
  pv_daily_kwh
  total_charge_kwh
  total_discharge_kwh
  payload_hash, quality_state, quality_reason

daily_energy_rollups
  site_id, asset_id, local_date, timezone
  pv_generation_kwh
  battery_charge_kwh
  battery_discharge_kwh
  first_sample_id, last_sample_id
  calculation_version, completeness, warnings
```

Use provider timestamps for freshness and interval attribution. `received_at_utc`
is audit metadata only. Never replace a missing metric with zero. A cumulative
counter decrease triggers `counter_reset`; the rollup is incomplete until a safe
baseline is reconstructed.

### Planning contract

The optimizer request must receive:

- latest validated measured SOC and its source timestamp;
- asset min/max SOC and charge/discharge limits;
- configured reserve SOC and emergency floor;
- complete next-day price series and common objective currency;
- charge/discharge efficiency;
- degradation cost in the same currency;
- current Deye battery/system/TOU configuration hash;
- site import/export limits and whether grid charging/export is contractually
  allowed.

An optimizer result is advisory until projected, validated and approved.

### Six-period TOU projection

Deye documents six ordered TOU intervals. A 24-step EMS schedule cannot be copied
losslessly when it has more than six transitions. The projector must either:

1. solve with a maximum-six-segment constraint; or
2. deterministically merge adjacent windows and recalculate lost economics.

Option 1 is recommended. The preview must show the unconstrained optimum, the
executable six-period plan and the economic delta. Activation is blocked if the
projection violates SOC/power limits or exceeds a configured loss threshold.

## Chat tool surface

Expose business operations, never raw HTTP:

- `get_bess_state(site_id)`;
- `get_daily_energy(site_id, local_date)`;
- `get_tomorrow_prices(site_id)`;
- `preview_day_ahead_plan(site_id, delivery_date)`;
- `explain_plan(plan_id)`;
- `request_plan_activation(plan_id)`;
- `get_activation_status(activation_id)`;
- `cancel_pending_activation(activation_id)`.

Only `request_plan_activation` can lead to a write. It creates an awaiting-approval
record; it does not call Deye. Approval must bind the exact plan hash, target,
configuration hash, expiry and approver identity.

## Guardrails and state machine

```text
draft -> validated -> awaiting_approval -> approved -> queued
      -> rejected                         -> expired

queued -> dispatched -> acknowledged -> verified
       -> failed      -> unknown       -> rollback_requested -> rollback_verified
```

Mandatory gates before queueing:

- correct site/asset/device binding;
- fresh SOC, device timestamp and configuration snapshot;
- no inverter/BMS fault and device online;
- SOC, reserve, temperature, charge/discharge power and grid limits;
- complete price series in one currency;
- delivery date/timezone/DST correctness;
- maximum six TOU periods in increasing order;
- plan hash and current configuration hash unchanged since approval;
- write feature flag, allowlisted endpoint and target;
- explicit, unexpired, single-use approval;
- idempotency key not previously executed;
- no active operator stop, safety event or overlapping activation.

Recommended idempotency key:

```text
sha256(site_id | asset_id | device_ref | delivery_local_date |
       executable_plan_hash | base_configuration_hash)
```

Deye commands return an `orderId`; HTTP/API success means accepted, not applied.
The adapter must poll order status to a terminal result and then read `/config/tou`
back. `verified` requires semantic equality with the approved plan. Timeout becomes
`unknown`, not `failed`, because blind retries could duplicate a successful command.

Before writing, store the full prior TOU snapshot. Rollback is a new approved or
policy-authorized command using that snapshot; it is never an unaudited hidden
side effect. If verification fails, block further automatic writes and request
operator action.

## Security

- Store app secret, account password and tokens in a secret provider, not appsettings,
  source files, prompts, model context, traces or audit payloads.
- Split read and write credentials/applications if Deye supports it.
- Redact Authorization, token, password, app secret, device serial and station ID
  from structured logs; store stable internal references instead.
- The LLM receives a bounded context containing measurements, plan summaries,
  warnings and opaque IDs only.
- Reject arbitrary endpoint names, custom Modbus content and free-form parameter
  dictionaries at the chat boundary.
- Require authenticated operator identity and role for approvals.
- Add a global kill switch and a per-site write-enable flag, both default off.

## Phased backlog and acceptance criteria

### Phase 0 — stabilize and merge read paths

- Isolate/commit the existing uncommitted Deye, ENTSO-E, site and orchestration
  work in reviewable branches; do not merge the whole dirty checkout blindly.
- Correct Deye response DTOs, provider timestamps, null handling, metric validation,
  freshness and source-health behavior.
- Correct ENTSO-E bidding-zone configuration and currency model.
- Feed measured SOC into each optimization request.

Acceptance: deterministic adapter tests use official response shapes; stale or
missing data cannot become valid zero/default data; Ukrainian prices and all
objective components use one declared currency.

### Phase 1 — daily observation and reporting

- Poll Deye latest/energy counters conservatively and persist raw samples.
- Calculate versioned daily rollups with reset/gap detection.
- Add read-only chat/API queries.

Acceptance: for a selected local day, generation, charge and discharge can be
reproduced from stored sample IDs; missing intervals are visible; no command-sink
dependency exists.

### Phase 2 — prices and dry-run planning

- Implement resilient ENTSO-E import plus official OREE reconciliation/fallback.
- Trigger after next-day publication, retry until complete, cache the last valid
  immutable series.
- Extend optimizer input with measured SOC and currency-consistent costs.
- Build and explain an advisory plan and economics.

Acceptance: 23/24/25-hour DST fixtures, gaps/duplicates/503/rate-limit fixtures,
replay-identical plans, and no writes.

### Phase 3 — executable TOU projection

- Implement maximum-six-period planning/projection.
- Read current Deye configuration and produce an exact diff and rollback snapshot.
- Add policy validation and proposal/approval state.

Acceptance: every executable plan has at most six ordered periods, survives
roundtrip serialization and shows its economics delta versus the unconstrained
optimum; expired or changed-base approvals cannot queue.

### Phase 4 — controlled sandbox activation

- Implement only TOU update/switch, order polling and read-back behind disabled-by-
  default feature flags.
- Test against a non-production device or vendor sandbox with deliberately small,
  reversible settings.

Acceptance: idempotent retry, accepted-but-not-applied, timeout/unknown, conflict,
offline, partial failure, read-back mismatch and rollback paths are proven. No
production device is in scope.

### Phase 5 — limited production pilot

- Enable one site with named approvers, low power/SOC envelope and mandatory daily
  approval.
- Monitor source freshness, command latency, mismatch rate, economic forecast error
  and manual interventions.

Acceptance: operator runbook, kill-switch drill, rollback drill, audit export and
pilot sign-off. Automatic standing approval is a later, separate decision.

## Tests required before any production write

- Official Deye payload fixtures for every used read/write/response type.
- Authentication expiry, 401 refresh, 429 + `Retry-After`, 5xx and network timeout.
- Cached/stale `lastUpdateTime`, missing SOC, missing counters and counter reset.
- Sign-convention reconciliation among battery, charge and discharge power.
- 23/24/25-hour price days, currency mismatch, gaps, duplicates and revisions.
- Measured-SOC planning and all existing optimizer/safety regressions.
- Six-period projection feasibility and economic-delta threshold.
- Approval plan-hash/config-hash binding, expiry, replay and concurrency.
- Asynchronous Deye accepted/failed/unknown order handling and read-back mismatch.
- No raw secret or device identifier in logs, agent context or audit export.
- Architecture test: agent/orchestrator cannot reference a raw Deye HTTP client or
  `IBatteryCommandSink` directly.

## Consequences

Positive:

- existing optimizer and safety mechanisms remain authoritative;
- daily reporting is available before write control;
- the agent remains useful without being a privileged raw-command channel;
- every activation is reproducible, attributable and reversible;
- cloud latency is handled as asynchronous supervisory control.

Trade-offs:

- six-period Deye TOU constraints may reduce theoretical arbitrage profit;
- the workflow requires additional persistence and approval state;
- daily counter accuracy depends on sufficiently frequent, timestamp-correct
  sampling until history API access is resolved;
- first production activation remains manual and deliberately slower.

## Required operator decisions

Implementation can proceed through read-only Phase 2 without these answers. Phase
3/4 activation design cannot be finalized until the operator confirms:

1. exact inverter model, firmware and observed meaning/range of all six TOU fields;
2. whether grid charging and grid export are contractually allowed, with import,
   export, charge and discharge caps;
3. minimum reserve SOC, emergency SOC floor and acceptable daily cycle/degradation
   cost in `UAH/kWh`;
4. whether each daily activation requires one-time approval, and who may approve;
5. acceptable maximum loss between unconstrained and six-period executable plans;
6. availability of a sandbox/non-production inverter for the first write test;
7. official source/license preference for OREE fallback and price treatment
   (without VAT, VAT-inclusive, and any tariffs/taxes to include).

