# Deye TOU — 4 windows automation runbook

Contract version: `1.0.0`

This document is the authoritative operating contract for the scheduled
automation `TOU BESS Deye — 4 вікна`. The automation prompt is only a
bootstrap: it must read this file completely on every run and must not replace
these rules with assumptions from chat history or `memory.md`.

## Scope and schedule

Use the `Europe/Kyiv` timezone. This automation only applies one of four saved
TOU payloads:

| Switch time | Window | Controlled period | Scenario date |
| --- | --- | --- | --- |
| 23:55 | Z1 | 00:00–06:00 | next local calendar date |
| 05:55 | Z2 | 06:00–12:00 | current local calendar date |
| 11:55 | Z3 | 12:00–18:00 | current local calendar date |
| 17:55 | Z4 | 18:00–24:00 | current local calendar date |

Normal scheduler or startup delay does not change the selected window. Use the
scheduled trigger that just fired (for example, a run beginning at 11:57 still
applies Z3); never advance to the next window merely because execution started
a few minutes late.

Do not calculate prices, download OREE data, create a daily plan, or poll the
station at 15:00. Daily planning is a separate automation.

## Working directory and inputs

Work only in `C:\Users\admin\temp\bess-ems` and use the saved file
`config/scenarios/deye-tou-YYYY-MM-DD.json` selected by the table above.

Before any network write, require all of the following:

- `payloadReady` is `true`;
- `target_date` exactly matches the selected scenario date;
- the scenario contains exactly four windows;
- the selected window exists and contains exactly six continuous intervals;
- every interval contains all required Deye fields;
- the prebuilt runner, its DLL, the environment file, and the scenario exist.

Any missing or invalid prerequisite is fail-closed without a TOU write.

## Time semantics and payload

The `time` field in the scenario and Deye API is the **start** of the interval
in `HH:mm` form. Keep every start-time/settings pair unchanged. Do not shift
power, SOC, or modes to the next boundary. The adapter may only rotate the
`00:00` row to the final position in the six-row wire payload.

If master `config/tou` does not expose `enableSell`, omit that unsupported field
from the write and comparison. Never treat cached `config/tou` as proof of the
actual inverter state.

## Runner invocation

Do not build during a scheduled switch. Do not use `dotnet run`, a global
runtime, `C:\Program Files (x86)\dotnet\dotnet.exe`, `DOTNET_ROOT`, or a
modified `PATH`.

From the repository root invoke only the prebuilt runner with local x64 .NET
10:

```powershell
.\.dotnet-x64\dotnet.exe .\tools\DeyeTouSwitchRunner\bin\Debug\net10.0\DeyeTouSwitchRunner.dll --env .\.env --scenario <scenario-path> --window <Z1|Z2|Z3|Z4>
```

The scheduled agent must not synthesize or modify a payload during switching.

## Live prewrite gates

The runner must perform a fresh live preflight immediately before a write:

- exactly two expected inverters and both online;
- configured master identified; TOU is read and written only on master;
- telemetry age is at most 600 seconds;
- master BMS voltage and BMS SOC are present;
- master BMS SOC is at least 30%;
- no active alarm;
- master power is at most 80 kW;
- aggregate parallel-system power is at most 160 kW.

Failure of any gate is fail-closed without a write.

## Write and verification

The only TOU mutation is `order/sys/tou/update`. Poll its order for at most 45
seconds. Status `666` alone is not proof that the requested settings are active.

Verify the actual master inverter state using
`strategy/dynamicControl/read` followed by
`strategy/dynamicControl/readResult` for that read order ID.

A completed `readResult` is an immutable snapshot. If it does not match the
payload, every later verification attempt must create a **new**
`strategy/dynamicControl/read` order and inspect the result for that new order
ID. Never repeatedly poll a completed old read order expecting its values to
change.

Compare all six start times, supported mode flags, SOC, voltage, and power.
Deye power quantization down to 10 W is permitted; changing interval boundaries,
modes, or SOC is not.

## Retry and fail-closed rules

- If an error occurs before the first actual TOU write, allow at most three
  complete attempts, each with a fresh preflight.
- Once `order/sys/tou/update` has been called, never automatically repeat the
  write for timeout, unknown status, or mismatch. Only additional fresh
  live-read orders are allowed.
- Activate fail-closed only after the current runner confirms mismatch using
  multiple fresh read orders.
- A later exact live read-back match clears the earlier mismatch as a false
  verification result.
- Do not reconstruct a lock from old chat messages or `memory.md`.
- An explicit user instruction `розблокуй write` or `повтори` unlocks one full
  run for the next applicable window. A new confirmed mismatch, timeout, or
  unknown result locks writes again.

## Reporting and secrets

Report the selected date/window and one of: applied and verified, blocked before
write with reason, or write accepted but not verified. Never expose credentials,
station ID, inverter serial numbers, tokens, or raw environment contents.

## Change control

Do not edit this runbook during a scheduled switching run. Changes are made in
the development branch, reviewed, copied to the operational checkout, and then
the contract version is incremented. If this file is missing or cannot be read
completely, do not write to Deye.
