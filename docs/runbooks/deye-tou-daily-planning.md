# Deye TOU — daily planning runbook

Contract version: `1.4.0`

This document is the authoritative operating contract for the scheduled
automation `TOU BESS Deye — планування 15:00`. The automation prompt must read
this file completely on every run and must not replace these rules with old
chat history or `memory.md`.

## Scope

At 15:00 `Europe/Kyiv`, build and save the complete TOU scenario for the next
local calendar date. A user may explicitly request a same-day recovery scenario
when the current day's scenario is missing; follow the recovery rules below.
This task is planning-only. Never call
`order/sys/tou/update`, `order/sys/tou/switch`, or any other Deye write command,
and never change master or slave.

Work in `C:\Users\admin\temp\bess-ems`.

## Operation audit log

For every planning run, append an audit record immediately after every agent
operation that reads or changes external/local state or invokes a tool/API
(including commands, optimizer execution, and artifact writes/reads), whether
it succeeds or fails. Log actions and sanitized outcomes, not private internal
reasoning. Also
append a `started` record before an operation that may take significant time.
The audit append itself is excluded to avoid recursive logging. Use the
append-only JSON Lines file `logs/deye-tou-agent-audit.jsonl`, shared with the
TOU runner. Each record must use the runner-compatible fields
`schema_version`, `timestamp_utc`, `run_id`, `scenario_date`, `window`,
`payload_sha256`, `action`, `phase`, `outcome`, `endpoint`, `http_status`,
`elapsed_ms`, and `details`; use `null` for inapplicable fields. Use one
consistent run ID for the full planning run and `window: null` for planning
events. Record concise operation names, result/status, counts or validation
summaries, and safe error type/message where useful. Do not log full tool
outputs, request/response bodies, credentials, tokens, station IDs, serials,
environment values, or sensitive query parameters.

Append and flush each record before starting the next substantive operation.
If the audit file cannot be appended or flushed, stop the run; do not continue
to another Deye read or mark a scenario executable. Record the failure in the
sanitized blocked report when possible. Do not overwrite or truncate prior
audit entries.

## Runtime

Use only repository-local x64 .NET 10. Do not use a global runtime, `dotnet run`,
`DOTNET_ROOT`, or a modified `PATH`. Run the optimizer from the repository root
with exactly:

```powershell
.\.dotnet-x64\dotnet.exe .\tmp\bess-deye-daily\PriorityOptimize\bin\Debug\net10.0\PriorityOptimize.dll
```

Verify that the local host and DLL exist before planning. If either is missing
or the optimizer fails, create a sanitized blocked report and do not synthesize
an executable scenario.

The optimizer must consume validated target-date prices and current model inputs.
A binary that embeds day-specific prices, voltage, initial SOC, or another
run-specific value is not valid for that run. Before execution, atomically write
the sanitized inputs to `config/scenarios/deye-tou-optimizer-input.json`. The
input must include the target date, 24 hourly prices, capacity, initial SOC,
optimization start hour, efficiency, SOC bounds, and aggregate power limits.
Run the exact command above from the repository root, capture structured output,
and validate its target date and input digest against the current input. Remove
the temporary input after successful validation and log every write, execution,
read, and removal. If the optimizer does not support these inputs, block; do not
reuse output for another date.

## OREE prices

Download the official OREE DAM/IPS XLS for `target_date`; at a month boundary,
select the month containing `target_date`. Validate the Europe/Kyiv 23/24/25
hour scale, no missing or duplicate slots, UAH/MWh currency, exact target row,
and complete prices.

## Read-only Deye/BMS snapshot

Perform one read-only planning poll of master Deye. Collect BMS voltage, all BMS
SOC channels, BatteryRatedCapacity where available, online state, telemetry
timestamps, and master `config/tou` baseline. Through `station/device` and
`device/latest`, require exactly two expected online inverters.

The planning telemetry freshness limit is **600 seconds** for each inverter.
An age from 0 through 600 seconds inclusive is fresh. Do not substitute the old
300-second limit from scripts, chat history, or `memory.md`. Record the actual
ages and `telemetryFreshnessLimitSeconds: 600` in the scenario metadata.

Do not call `config/tou` for slave. Slave response `2106001` is not evidence of
a broken parallel link. The switching runner performs a separate fresh live
prewrite before any later write.

## Battery model

Use two parallel inverters, four BMS strings of 314 Ah, capacity
`current BMSVoltage × 4 × 314 / 1000` kWh, aggregate charge/discharge limit 160
kW, and master TOU power limit 80 kW. The SOC corridor is 30–100%, with 30% as
the UPS reserve.

Use the expected target-day starting SOC derived from the preceding saved
scenario when that tracking data is valid; retain the live BMS SOC separately
as the planning snapshot. If no valid preceding trajectory exists, use the
fresh mean of all BMS SOC channels clamped to 30–100%.

## Manual same-day recovery

Recovery mode is allowed only after an explicit user request to recover a
missing scenario, with `target_date` equal to the current `Europe/Kyiv` date.
Do not use it for a past or future date, or replace an existing valid executable
scenario. Recovery remains planning-only. Creating a file does not apply or
retroactively execute any missed Z1–Z4 window.

Validate all 24 prices for the target date and take one read-only Deye/BMS
snapshot using the same 600-second freshness limit. Use the fresh mean of all
BMS SOC channels, clamped to 30–100%, as the live recovery starting SOC; do not
substitute the previous day's projected ending SOC.

Optimize only whole local hours that have not started at the snapshot time. If
the snapshot is exactly on an hour boundary, that hour may be included;
otherwise start at the next whole hour. If no whole hour remains or the
optimizer cannot accept the resulting `startHour`, create a blocked report. Do
not infer actions or SOC for elapsed or partially elapsed hours. Preserve all
24 validated prices in the artifact; mark earlier hours `elapsed_not_planned`,
with zero planned charge/discharge and no fabricated SOC trajectory.

Keep the four standard windows and activation times. In recovery metadata, mark
each activation time earlier than the request as missed. Use safe gray-zone
baseline settings for those windows and do not encode retroactive actions.
Future windows may contain the optimized remaining-day plan. The planning run
must never invoke the switching runner.

## Optimization policy

Maximize full-day margin across every hourly price while respecting SOC,
efficiency, capacity, and power limits. Charge in the cheapest hours and
discharge in the most expensive. Never reserve an earlier cheaper discharge
hour at the expense of a later more expensive hour. With limited energy, higher
prices have strict priority. For equal charge prices choose the earliest hour;
use a deterministic order for equal discharge prices. Early pre-charge is
allowed only when it supports a more profitable later discharge without
crossing the 30% reserve.

## Four saved TOU windows

Split the single optimized trajectory into:

- Z1: 00:00–06:00, activation 23:55 on the preceding date;
- Z2: 06:00–12:00, activation 05:55;
- Z3: 12:00–18:00, activation 11:55;
- Z4: 18:00–24:00, activation 17:55.

Every window contains exactly six ordered Deye intervals covering the full
00:00–24:00 day. In both the scenario and Deye API, `time` is the **start** of
the interval. Keep start-time/settings pairs unchanged. The switching adapter
only rotates the `00:00` row to the last wire position; it does not shift power,
SOC, or modes to another boundary.

Every active and gray-zone interval explicitly contains `time`,
`enableGeneration`, `enableGridCharge`, `enableSell`, master `power`, `soc`, and
`voltage`. Active charge targets 100% SOC. Active discharge and reserve never
go below 30%. Expand gray zones deterministically from the safe baseline and do
not permit a cheap earlier discharge to consume energy reserved for a more
expensive active slot.

### Six-slot representability

Deye's six rows are a hard optimization/output constraint, not a reason to
discard an otherwise valid daily plan after optimization.

For every window:

1. Coalesce adjacent hours whose complete Deye settings are identical.
2. If more than six settings runs remain, deterministically re-project or
   re-optimize the hourly trajectory under equality constraints that make it
   representable in six rows. Preserve energy balance, SOC bounds, the 30% UPS
   reserve, power limits, price priority, and start-time semantics.
3. First prefer adjacent same-direction hours with equal prices. Redistribute
   their combined energy to one common power value within the limits. This
   preserves revenue/cost and SOC while removing a breakpoint. For example,
   adjacent equal-price discharges of 160 kW and 141.720672 kW may both become
   150.860336 kW.
4. If equal-price consolidation is insufficient, solve the smallest-loss
   constrained projection and record its margin delta. Never allow a cheaper
   discharge hour to displace a more expensive one, never cross SOC limits, and
   never silently shift settings across an hourly boundary.
5. If fewer than six runs remain, split any constant run at deterministic hour
   boundaries without changing its settings until exactly six rows exist.

Only block with `TOU_WINDOW_CARDINALITY_CONFLICT` when no safe six-row
projection exists after this constrained representability step. Validate the
projected trajectory again and store both the original optimizer objective and
the final representable objective/margin delta.

## Artifact and validation

Atomically write `config/scenarios/deye-tou-YYYY-MM-DD.json` with metadata, the
BMS snapshot, optimization result, all hourly prices/actions, planned SOC
trajectory, and all four six-slot payloads. Set:

- `payloadReady: true`;
- `executionStatus: pending_live_prewrite`;
- `requiresLivePrewrite: true`.

Re-read the saved file and validate target date, four windows, six intervals per
window, continuous full-day coverage, start-time semantics without shifted
settings, all required fields, SOC 30–100%, master power at most 80,000 W,
aggregate power at most 160 kW, and the expensive-hour priority invariant.

If prices, telemetry, model inputs, optimizer output, or validation are invalid,
write a sanitized `.blocked.json` report instead of an executable scenario.
For recovery artifacts, include `metadata.recoveryMode: true`, the explicit
request time, snapshot time, `recoveryStartHour`, and the IDs of missed
activation windows. Do not store fabricated SOC values for elapsed hours.
Never expose credentials, tokens, station ID, serial numbers, or raw environment
contents.

## Daily price and operating-plan chart

After saving, re-reading, and validating each daily scenario, show its chart in
the run's final response without requiring a separate user request. This also
applies to explicitly requested same-day recovery plans. Read chart data only
from the final saved scenario; do not re-optimize, change the scenario, poll
Deye again, or invoke the switching runner to prepare the chart.

Use the same three vertically stacked, aligned hourly plots as the approved
10.10.2026 chart:

- Official DAM/IPS prices in UAH/MWh.
- Aggregate power of both inverters in kW: charge below zero, discharge above
  zero, and idle at zero. Do not substitute master power or energy for power.
- Planned SOC in percent, including the start/end trajectory and a clearly
  labeled 30% UPS-reserve reference.

Label the target date and Europe/Kyiv time scale, preserve every validated hour,
and identify the chart as a plan, not actual execution. In recovery mode, show
elapsed/not-planned hours as such and leave their unknown SOC unplotted. Do not
fabricate trajectories or present a blocked report as a completed plan.

Use the visualize skill when available, with a responsive, theme-aware chart,
series legend toggles and shared hourly price/power/SOC hover details. Save a
date-specific fragment named `deye-tou-prices-plan-YYYY-MM-DD.html` in the
thread's authorized durable visualization directory, or an authorized project
output directory when that is unavailable. Read it back and verify its data
against the saved scenario, units, signs, date, rendering and interactions;
include its inline visualization reference in the final response. Audit chart
reads, writes and checks under the same run ID. If chart generation or rendering
fails, report and audit the presentation failure explicitly; do not invent a
chart or silently omit it, and do not change an already validated scenario.

## Change control

Do not edit this runbook during a scheduled planning run. Make changes in the
development branch, review them, copy the file to the operational checkout, and
increment the contract version. If this file is missing or cannot be read
completely, do not create an executable scenario.
