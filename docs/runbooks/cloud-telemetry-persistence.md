# FusionSolar, Deye Cloud and Telecontrol measurement history

With `Bess:PersistenceConnectionString` configured, all three cloud adapters write
through the existing PostgreSQL stores during every poll. No new migration is
needed; migrations through `0012_site_measurement_status_rows` must be applied.
Without PostgreSQL configuration the host uses its in-memory measurement store.

## Tables and metrics

`site_measurements` contains instantaneous readings (`interval_seconds=0`):

| source | instrument_type | metric | unit |
|---|---|---|---|
| fusionsolar | pv | pv_power | kW |
| deye_cloud | pv | pv_power | kW |
| deye_cloud | pv | irradiance | W/m2 |
| deye_cloud | load | load_power | kW |
| deye_cloud | grid | grid_power | kW |
| deye_cloud | battery | battery_power | kW |
| deye_cloud | battery | battery_soc | % |
| deye_cloud | battery | dc_voltage | V |
| deye_cloud | battery | dc_current | A |
| telecontrol | chp | chp_power | kW |

FusionSolar instrument IDs are station codes; station power is the sum of all
string inverters, requiring complete responses. Deye instrument IDs are the
configured station ID, or device serial when no station ID is configured.
Deye metadata includes the internal EMS asset ID. No credentials or raw cloud
payloads are included in stored metadata.

FusionSolar site assignments come from `FusionSolar:StationSiteIds:<stationCode>`
or the existing `Dashboard:Sites` PV bindings. Deye uses `DeyeCloud:SiteId` or the
dashboard battery binding matching `DeyeCloud:AssetId`. Conflicting bindings fail
configuration rather than writing to the wrong site. Unassigned sources are
still archived as `unassigned:fusionsolar:<stationCode>` or
`unassigned:deye_cloud:<stationId-or-deviceSn>`; these are source buckets, not
physical sites. Changing a binding does not relocate old records.

FusionSolar stores measurement quality (`valid`, `substituted`, `stale`,
`source_error`) and a reason. When cloud sample time is absent, observation time
is recorded with `substituted`. Missing Deye values use null with `missing`;
failed polls use null with `source_error`, never zero generation.
Repeated FusionSolar samples with the same timestamp are upserted.

Deye also archives its existing battery DTO in `telemetry`, keyed by `asset_id`
and `recorded_at`, through `ITelemetryRepository`. These rows use substituted
quality because the DTO currently uses poll time and defaults SOH, reactive
power and temperature (100%, 0 kvar, 25 C). Missing battery fields default to
zero in this legacy DTO and are explicitly flagged in `data_quality_reason`;
use `site_measurements` for nullable measured values. Live control snapshots
retain their existing behavior.

## Telecontrol

Every Telecontrol poll archives one `chp_power` reading in `site_measurements`,
with `source=telecontrol`, `instrument_type=chp`, and `instrument_id` equal to
the numeric provider device ID (for example `5552`). It archives the measured
generation power from `Leistung`, never the setpoint or an energy counter.

The row's `metadata_json` retains all parsed parameters (`name`, `unit`,
`text_value`, `numeric_value`, `available`) and all messages (`name`, `code`,
`message_type`) as arrays. Duplicate parameter names remain separate array
entries; text/status parameters are retained even when they have no numeric
value. Metadata also contains `asset_id`, `received_at`, `source_timestamp`,
`source_timezone`, `clock_confirmed` and the quality `reason`. These are parsed
telemetry fields; authentication responses, credentials and bearer tokens are
not included. Parameters/messages are snapshot metadata, not individual metric
rows.

The stored timestamp is the parsed provider `packageDateTime`; missing or invalid
time falls back to receipt time with `source_error`. Provisional clock conventions
remain `substituted`, aged readings remain `stale`, and failed polls are archived
with null power and `source_error`. Live snapshots are updated before persistence.
The existing measurement upsert key keeps one row for repeated identical source
timestamps, with the most recent snapshot metadata.

Site assignment uses `Telecontrol:SiteId` or the dashboard `kind=chp` binding
matching `Telecontrol:AssetId`. Conflicting assignments fail configuration.
Without a binding, records go to `unassigned:telecontrol:<deviceId>`. The example
configuration binds device 5552 to the existing `site-khlibzavod-5` (Novobudov 6).

Storage failures are logged, and live polling continues. There is no durable
retry queue: samples during database outages can be lost. No historical backfill
or per-inverter FusionSolar archive is performed.

## Inspect records

```sql
SELECT site_id, source, instrument_id, timestamp, metric, value, unit, quality
FROM site_measurements
WHERE source IN ('fusionsolar', 'deye_cloud', 'telecontrol')
ORDER BY imported_at DESC
LIMIT 100;

SELECT asset_id, recorded_at, soc_percent, active_power_kw,
       data_quality_state, data_quality_reason
FROM telemetry
ORDER BY recorded_at DESC
LIMIT 100;
```

This change is implemented in the main source tree. The preserved deployment
snapshot at `artifacts/telecontrol-20261008/source` is not updated automatically;
running that older image does not activate this implementation. Rebuild and
deploy the updated source to enable database collection on the server.
