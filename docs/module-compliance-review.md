# Module Compliance Review

Date: 2026-06-08

Scope: review current modules against `spec/architecture.md`,
`spec/lastenheft.md`, `docs/site-level-optimization-plan.md`,
`docs/orchestrator-plan.md`, `data_atribute.md`, `memory.md`, and ADRs 0001,
0007, 0008 and 0011.

Main constraint: the battery EMS calculation/control core is not changed by this
review.

## Summary

The project structure is broadly aligned with the documented architecture:

- Domain remains framework-free.
- Application remains a single `BatteryEms.Application` project with Site and
  Orchestration namespaces, consistent with ADR 0011.
- Runtime persistence uses the Dapper/Npgsql + DbUp path, consistent with ADR
  0001.
- Host is the composition root that references driven and driving adapters.
- Architecture boundary tests pass after this review.

## Changes Made

### Site Measurement Contract

`SiteMeasurementReading.Value` now supports status-only rows with `null` value
for:

- `missing`
- `source_error`
- `invalid`

Numeric readings still require finite values. This aligns code with
`data_atribute.md`, where missing or source-error values must not be converted
to zero.

Persistence migration added:

- `0012_site_measurement_status_rows.sql`

### Site Prepared Settings

`DefaultSiteSettingsPreparationUseCase` now accepts the canonical metric
`inverter_yield` and keeps compatibility with legacy/raw `inverterYield`.

Null status-only measurement values are ignored by power/energy calculations.

### Open-Meteo Analyzer Cleanup

Pure helper methods were marked static so the architecture test project can
build under warnings-as-errors.

## Module Status

| Area | Status | Notes |
|---|---|---|
| Domain | Aligned | No adapter, persistence, HTTP or source-specific dependency found. |
| Application core | Aligned | Site and Orchestration stay as namespaces, not new projects. |
| Site Application | Improved | Measurement null/status semantics and `inverter_yield` naming now match the data standard. |
| Orchestration Application | Aligned foundation | Ports, in-memory stores and use case stay in Application; no command sink dependency. |
| Persistence adapter | Improved | Dapper/DbUp path preserved; site measurement status rows now persist. |
| ASKUE adapter | Mostly aligned | Collects source data and writes through Application store. Polling hosted service is a follow-up architecture cleanup. |
| FusionSolar adapter | Mostly aligned | Per-station source handling remains adapter-local. Polling hosted service is a follow-up architecture cleanup. |
| Deye Cloud adapter | Mostly aligned | Battery telemetry remains in battery path, site data remains in site telemetry path. |
| ENTSO-E adapter | Aligned | Price source remains source adapter behind Application market ports. |
| Open-Meteo adapter | Mostly aligned | Forecast collection is source-specific; hosted refresh scheduling is a follow-up architecture cleanup. |
| Worker/API | Aligned for existing battery flow | Orchestration API/Worker endpoints are still follow-up work. |

## Remaining Follow-Ups

1. Move or wrap ASKUE, FusionSolar and Open-Meteo polling hosted services under
   Worker/Orchestration control, or explicitly mark them as temporary standalone
   collectors until the orchestrator scheduler owns module timing.
2. Add architecture tests for the new documented rules:
   - Application.Orchestration must not reference `IBatteryCommandSink`.
   - Source adapters must not reference `IOrchestrationUseCase`.
   - Agent proposal code must not reference secret-bearing option types.
3. Add API/Worker orchestration endpoints from `docs/orchestrator-plan.md`.
4. Decide whether source-error rows should be written for every FusionSolar
   per-station failure or only represented in data-balance status.

## Verification

- `BatteryEms.Application.Tests` focused Site tests: 8/8 passed.
- `BatteryEms.Adapters.Persistence.Tests` focused migration resource tests:
  4/4 passed.
- `BatteryEms.ArchitectureTests`: 29/29 passed.
- `BatteryEms.Persistence.IntegrationTests` focused site measurement status
  row roundtrip: 1/1 passed against local test PostgreSQL.
