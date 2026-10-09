# All sites dashboard

The operator dashboard has a view selector: one site or **Усі площадки · All sites**. The fleet view calls read-only `GET /sites/overview`; the browser does not add power figures itself. It displays fleet generation/consumption, per-site contributions, source coverage and observation times. Single-site equipment, profit and export cards are hidden in fleet mode.

## Explicit membership

`Dashboard:Sites` is a read-only dashboard projection of the approved physical site composition. It does not automatically equate an account, a battery asset or a provider plant with a physical site. Each entry has `SiteId`, `Name` and `Sources`; each source has:

- `PhysicalId`: the canonical physical component used to prevent duplicate generation or consumption.
- `TelemetryId`: the primary telemetry-store key for that component.
- `Kind`: `pv`, `chp`, `battery`, or `load`.

PV and CHP read the source's normalized `PvPowerKw` field. Load reads `LoadPowerKw`. Batteries read `ActivePowerKw` through `IBatteryStatusQuery`; only `max(power, 0)` contributes to generation. A hybrid inverter's PV and battery are separate components; its combined AC output must not be counted again as PV. For a shared installation reported by multiple connectors, select a primary telemetry source for each physical component. Distinct installations on one site must all be included once.

Duplicate physical components across generation roles, duplicate bindings within a role and duplicate site IDs return HTTP 503 rather than a doubled sum. Sources missing configuration are not discovered or implicitly assigned.

`Dashboard:MembershipConfirmed` defaults to false. While false, all-source totals omit `power_kw` and have status `membership_pending`. Individual rows remain visible for reconciliation. Set true only after shared sources and site membership are resolved; the user's statement on 2026-10-08 confirms that there are overlapping installations but does not yet identify them.

## Availability

Freshness checks both measurement and receipt time (10 minutes maximum; future timestamps rejected). Only finite power with quality Valid or Substituted is displayed. Substituted source readings make the aggregate estimated; this read-only display does not promote their quality for equipment control. Invalid, missing or stale power stays unavailable. A measured zero remains zero. Negative load/PV/CHP is unavailable; negative battery power contributes a measured zero discharge.

Statuses are `complete`, `estimated`, `partial`, `unavailable` and `membership_pending`. If any site lacks a metric or source, the corresponding fleet metric is partial; a site's missing load cannot silently disappear from coverage. Counts describe source components, not verified distinct sites when membership is pending. Huawei real-time consumption and CHP real-time power remain unavailable in the current deployment.

The deployed provisional configuration lists Deye and the four Huawei provider groups separately, with membership unconfirmed. These five rows are not a claim that there are five independent physical sites. Equipment control/activation/broker switches remain disabled.

## Validation

Seven fleet API tests cover charge/discharge, PV/CHP/BESS sums, consumption, missing/stale data, measured zero, duplicate components/bindings, unconfirmed membership and a reader endpoint with an empty fleet. The deployed snapshot passes these plus its static UI test (8 total). The dashboard's 22 Node tests include fleet coverage, shared-source protection and switching views while a prior response is in flight.
