# FusionSolar: power telemetry and explicit site membership

The site model is defined in [site-level-optimization-plan.md](../site-level-optimization-plan.md), especially Multi-Site Architecture. A provider account may contain stations from several sites. A single battery asset does not establish a station's membership in a site.

`FusionSolar:StationCodes` is the deduplicated list of stations to collect. Each station is stored independently under its station code. By default (`UseDeviceTelemetry=true`), `getDevList` resolves station membership and `getDevRealKpi` reads string inverter (type 1) `active_power` in kW. Each station requires every listed inverter; meters, loggers and other stations are excluded. Device requests are batched at 100; topology is refreshed hourly. Energy fields and undocumented aliases are not power fallbacks. Legacy station KPI mode is opt-in (`UseDeviceTelemetry=false`) and accepts only `active_power`.

The live audit on 2026-10-08 confirmed that `getStationRealKpi` returns daily/monthly/lifetime energy, without `active_power` or `collectTime`. The former ~594 kW account sum was daily energy incorrectly labeled as power and must not be used. `getDevRealKpi` returned real inverter `active_power`, but omitted sample timestamps. Such readings retain poll observation time with quality `Substituted` and reason `fusionsolar-device-sample-time-unavailable-observed-at-poll-time`; they cannot qualify a fully validated site aggregate or control input. API `params.currentTime` is response time, not a device measurement timestamp.

`collectTime`, when available, is the measurement timestamp; the poll time is retained separately as `ReceivedAt`. Missing station-mode timestamps and future or invalid measurement timestamps are protocol errors. Measurements older than `MaxMeasurementAgeSeconds` (default 600) are stale. A valid measured zero remains zero. Failed or missing stations do not become zero.

To publish a site PV subtotal, explicitly configure both:

- `FusionSolar:AssetId`: the existing site telemetry key (legacy option name; do not infer it from a BESS asset).
- `FusionSolar:SiteStationCodes`: only the approved physical members of that site, each also included in `StationCodes`.

Without this membership, station collection continues but no site sum is published. An aggregate requires every configured member to have valid fresh power. Missing or stale members invalidate the subtotal instead of publishing a partial sum as Valid. Its measurement time is the oldest included member's time. Duplicate station records are reduced to the newest measurement; duplicate configured identifiers are not summed twice.

The checked-in `config/examples/site-balance.khlibzavod-5.json` currently has `status=draft_meter_roles_pending`, `fusionsolar.enabled=false` and an empty station list. The four stations accessible to the account are therefore not automatically assigned to Khlibzavod 5. Historic account-wide aggregation is not evidence of physical membership.

The main dashboard generation KPI must remain unavailable until the site's complete PV/CHP/BESS composition is established. A PV subtotal is not the site's total generation. Once membership and boundaries are confirmed, apply the requested rule: Deye PV + FusionSolar PV + CHP + sum(max(each BESS discharge power, 0)), without counting the same physical installation twice.

This change does not enable equipment commands. The server must retain `Worker:ControlEnabled=false`, Deye NoOp sink and all activation/broker write switches disabled.

Huawei's [device data interface reference](https://support.huawei.com/enterprise/mx/doc/EDOC1100306384/2630ae1b/device-data-interfaces) documents the device-list prerequisite, type-specific real-time requests and 100-device batches. The live cloud payload was audited separately; the API response clock must not be confused with a device sample timestamp.
