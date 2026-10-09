# Site Data Attribute Standard

Status: draft for BESS-EMS / SITE-EMS integration.

Purpose: every adapter must write raw and normalized site data with the same
identity fields, units, metric names, time rules, and quality attributes. This
keeps ASKUE, Deye Cloud, FusionSolar, ENTSO-E/RDN, and future sources usable by
the intermediate site settings layer and the upper SITE EMS.

## Architecture Fit

This standard is a data contract inside the existing BESS-EMS architecture:

- Application owns canonical records, validation, readiness and prepared values.
- Driven adapters own source-specific protocol details and raw-to-canonical
  mapping, but not site orchestration decisions.
- Persistence uses the existing PostgreSQL/Dapper/DbUp path from ADR 0001.
- Site and orchestration code stays inside `BatteryEms.Application` namespaces
  according to ADR 0011.
- `site_id` is the site coordination key; battery `asset_id` remains the key
  for battery schedules, telemetry and command ownership.
- Edge/safety control remains outside this data standard unless a later
  approved control slice defines a command contract.

## Core Principles

1. Never write secrets into data records, logs, memory files, reports, API
   responses, or agent context.
2. Store raw source identifiers, but normalize physical values into canonical
   units.
3. Missing values stay `null`; they must not be converted to zero.
4. Every measurement belongs to a `site_id`.
5. Every source value must keep `source`, `instrument_type`,
   `instrument_id`, `metric`, `unit`, `timestamp`, and `quality`.
6. Instant values and interval values must be distinguishable.
7. Energy and power must not be mixed silently.
8. All timestamps written to the database should be UTC. Local market-day
   meaning may be kept in metadata or request context.
9. Raw source semantics must be preserved in `metadata_json` when they matter.
10. Site-level prepared values are read-only derived values. They do not write
    battery commands and do not change the battery optimizer.

## Canonical Record Shape

Use `SiteMeasurementReading` as the common structure for source measurements:

```text
site_id
source
instrument_type
instrument_id
instrument_name
timestamp
interval
metric
value
unit
quality
group_parent_id
scale
metadata_json
```

Field rules:

| Field | Required | Rule |
|---|---:|---|
| `site_id` | yes | Logical site identifier, not battery `asset_id`. |
| `source` | yes | Lowercase adapter/source name: `askue`, `deye_cloud`, `fusionsolar`, `entso_e`, `manual`, etc. |
| `instrument_type` | yes | Physical/logical instrument family: `meter`, `pv`, `grid`, `battery`, `price`, `weather`, `inverter`. |
| `instrument_id` | yes | Source-side stable identifier: ASKUE point id, FusionSolar station code, Deye station id, ENTSO-E domain/product key. |
| `instrument_name` | yes | Human-readable name when known; otherwise repeat id. |
| `timestamp` | yes | UTC timestamp for instant value or interval start. |
| `interval` | no | Required for interval energy or average values, for example `00:30:00` or `01:00:00`. Null means instant reading. |
| `metric` | yes | Canonical metric name from the metric catalog below. |
| `value` | conditional | Finite numeric value for `measured`, `derived`, `estimated` and `stale` readings. Null is allowed only for explicit `missing`/`source_error`/`invalid` status records. |
| `unit` | yes | Canonical unit from the unit catalog below. |
| `quality` | yes | `measured`, `derived`, `estimated`, `stale`, `missing`, `source_error`, `invalid`. |
| `group_parent_id` | no | Optional grouping id from source hierarchy. |
| `scale` | no | Optional source scale/exponent when needed for audit. |
| `metadata_json` | no | Compact JSON for source-specific context, no secrets. |

## Canonical Units

Power:

| Canonical unit | Meaning |
|---|---|
| `kW` | Active power. |
| `kvar` | Reactive power. |

Energy:

| Canonical unit | Meaning |
|---|---|
| `kWh` | Active energy. |
| `kvarh` | Reactive energy. |

Voltage/current:

| Canonical unit | Meaning |
|---|---|
| `V` | Phase voltage or line voltage, context in metric name. |
| `A` | Current. |

Price/money:

| Canonical unit | Meaning |
|---|---|
| `UAH/MWh` | Market price from ENTSO-E/RDN. |
| `UAH/kWh` | Derived site optimization price if needed. |
| `UAH` | Monetary amount. |

Other:

| Canonical unit | Meaning |
|---|---|
| `%` | Percent values, including SOC/SOH/imbalance. |
| `degC` | Temperature. |
| `W/m2` | Irradiance. |

Conversion rules:

```text
W / 1000 = kW
Wh / 1000 = kWh
MWh * 1000 = kWh
UAH/MWh / 1000 = UAH/kWh
energy_kwh / interval_hours = average_power_kw
average_power_kw * interval_hours = energy_kwh
```

## Canonical Metrics

PV:

| Metric | Unit | Meaning |
|---|---|---|
| `pv_power` | `kW` | Instant or average PV active power. |
| `pv_energy` | `kWh` | PV generated energy over interval. |
| `inverter_yield` | `kWh` | Source KPI energy from inverter/station over interval. Prefer mapping to `pv_energy`; preserve raw name in metadata. |
| `irradiance` | `W/m2` | Solar irradiance. |

Load and grid:

| Metric | Unit | Meaning |
|---|---|---|
| `load_power` | `kW` | Site load power. |
| `load_energy` | `kWh` | Site load energy over interval. |
| `active_power_import` | `kW` | Power imported from grid. |
| `active_power_export` | `kW` | Power exported to grid. |
| `active_energy_import` | `kWh` | Energy imported from grid. |
| `active_energy_export` | `kWh` | Energy exported to grid. |
| `reactive_power_import` | `kvar` | Reactive import. |
| `reactive_power_export` | `kvar` | Reactive export. |
| `reactive_energy_import` | `kvarh` | Reactive import energy. |
| `reactive_energy_export` | `kvarh` | Reactive export energy. |

Three-phase voltage:

| Metric | Unit | Meaning |
|---|---|---|
| `phase_l1_voltage` | `V` | Actual L1 voltage. |
| `phase_l2_voltage` | `V` | Actual L2 voltage. |
| `phase_l3_voltage` | `V` | Actual L3 voltage. |
| `phase_imbalance` | `%` | Derived phase imbalance. |

Battery/site prepared values:

| Metric | Unit | Meaning |
|---|---|---|
| `battery_soc` | `%` | Battery state of charge. |
| `battery_soh` | `%` | Battery state of health. |
| `battery_power` | `kW` | Battery active power, positive discharge/export to site, negative charge. |
| `site_net_power` | `kW` | Derived site balance. Define sign convention in metadata. |

Prices:

| Metric | Unit | Meaning |
|---|---|---|
| `market_price_day_ahead` | `UAH/MWh` | RDN/DAM day-ahead price. |
| `market_price_intraday` | `UAH/MWh` | Intraday price if added later. |

## Quality Values

Use one of:

| Quality | Meaning |
|---|---|
| `measured` | Directly received from source. |
| `derived` | Calculated from measured data, for example kWh to average kW. |
| `estimated` | Filled by forecast/interpolation/model. |
| `stale` | Last known value older than accepted age. |
| `missing` | Source did not provide value. |
| `source_error` | Source returned an API/protocol error. |
| `invalid` | Value failed validation and must not be used for control. |

Rules:

- A missing numeric value is represented by no measurement row, a nullable
  field in a domain-specific record, or an explicit status row with
  `quality=missing` and `value=null`; never by `0`.
- If a source explicitly returns an error for an instrument, write either an
  error status record with `quality=source_error` and `value=null`, or log it
  when the target store has no status-row shape; do not invent a numeric value.
- Prepared site settings must downgrade quality when required inputs are
  stale/missing/invalid.

## Time and Interval Rules

1. Database timestamps are UTC.
2. `timestamp` for interval data is the interval start.
3. `interval` must be set for hourly/30-minute energy records.
4. `interval_end = timestamp + interval`.
5. Kyiv local market days are request semantics, not DB timestamp semantics.
6. DST handling must use `DateTimeOffset`; do not assume every local day has
   exactly 24 wall-clock hours.
7. ENTSO-E/RDN sparse points must be expanded before optimization.

For ENTSO-E/RDN:

- Missing point positions mean the previous price remains active.
- Store expanded hourly values for EMS use.
- Keep source document/position details in metadata when useful.

## Source Mapping Rules

### ASKUE

Source:

```text
source = askue
instrument_type = meter
instrument_id = ASKUE point id
instrument_name = ASKUE point name
```

Known ASKUE profiles:

| Source profile | Canonical metric | Unit |
|---|---|---|
| `apoz` | `active_energy_import` | `kWh` |
| `aneg` | `active_energy_export` | `kWh` |
| `ppoz` | `reactive_energy_import` | `kvarh` |
| `pneg` | `reactive_energy_export` | `kvarh` |

Rules:

- Respect selected period: 30-minute or 60-minute.
- Store `interval`.
- If deriving average power from energy:
  `active_power_import = active_energy_import / interval_hours`.
- Keep original profile name in `metadata_json`.
- The ASKUE API `/profile` value is the source profile value and must remain
  auditable in `site_consumption_readings`.
- The web monitoring path uses `POST /meter/{point_id}.html` and currently sits
  behind Cloudflare challenge/session checks. Browser-like requests may require
  runtime cookies such as `askue` and `cf_clearance` in addition to ordinary
  headers like `Origin`, `Referer`, and `X-Requested-With`.
- Transformer coefficients must not be hardcoded in the adapter. If a
  site/meter needs a transformation ratio, configure it per meter, for example
  `value_multiplier=400`.
- For a "same as ASKUE web monitor" export, convert source profile values to
  display values as `source_value * value_multiplier`. Example for meter 929:
  `0.0209 * 400 = 8.36` for the displayed `A+` value on a 30-minute row.
- For site-balance inputs, convert persisted ASKUE interval values to canonical
  energy as `source_value * value_multiplier * interval_hours`. If
  `interval_seconds` is missing, the fallback is `source_value *
  value_multiplier` for backward compatibility only.
- ASKUE `/points.scale` is a source hierarchy/orientation attribute for audit;
  it must be preserved in metadata and must not be used as an automatic
  physical unit conversion unless a later source-specific mapping explicitly
  defines it.
- Treat `value_multiplier` and `/points.scale` as different concepts:
  `value_multiplier` is the physical/display conversion ratio configured per
  meter, while `/points.scale` is only an orientation/audit attribute.

### FusionSolar

Source:

```text
source = fusionsolar
instrument_type = pv
instrument_id = stationCode, for example NE=129469793
instrument_name = stationCode unless station name is known
```

Known fields:

| Source field | Canonical metric | Unit | Note |
|---|---|---|---|
| `inverterYield` | `pv_energy` or `inverter_yield` | `kWh` | Hourly KPI energy. |

Rules:

- `/getKpiStationHour` returns hourly KPI records.
- `inverterYield` is energy for the hour, not true instant power.
- For one-hour intervals, prepared site settings may derive average
  `pv_power = inverterYield / 1h`.
- Poll station codes independently.
- If one station returns `failCode=20056`, continue other stations and mark
  that station as `source_error` or log the error.
- Do not write credentials, XSRF tokens, or cookies.
- Runtime configuration may inject browser session cookies for source access,
  but they must never be persisted in measurements, reports, logs, memory
  files, or API responses.

### Deye Cloud

Source:

```text
source = deye_cloud
instrument_type = pv/grid/load depending on field
instrument_id = station id or device serial
```

Known mappings:

| Source field | Canonical metric | Unit |
|---|---|---|
| `generationPower` | `pv_power` | `kW` |
| `totalSolarPower` | `pv_power` | `kW` |
| `consumptionPower` | `load_power` | `kW` |
| `totalLoadPower` | `load_power` | `kW` |
| station/grid power fields | `active_power_import` or `active_power_export` | `kW` |

Rules:

- Preserve missing site fields as `null`.
- Deye battery telemetry remains in the battery telemetry path.
- Site metrics from Deye go into site telemetry/measurement path only.
- The station/latest API may return cached values; keep source timestamp when
  available and mark stale values as `stale`.

### ENTSO-E / RDN Prices

Source:

```text
source = entso_e
instrument_type = price
instrument_id = domain + product + price kind
```

Canonical price record:

```text
metric = market_price_day_ahead
unit = UAH/MWh
timestamp = delivery interval start UTC
interval = 01:00:00
quality = measured
```

Rules:

- EMS accepts `UAH/MWh`; optimizer converts economics as needed.
- For site-level calculations, use `UAH/kWh` only as derived value.
- Missing ENTSO-E point positions must be expanded by carrying the previous
  price forward.
- Store `domain_code`, `document_type`, `business_type`, `currency`, and
  source position/range in metadata when available.
- `price_series` / `price_series_points` remain the canonical persistence
  model for market price horizons. Mirror prices into `SiteMeasurementReading`
  only when a site-level report needs the same normalized measurement shape.

## Prepared Site Settings Output

Canonical Application name: `SitePreparedSettings`.

`SitePreparedSettings` is the normalized, site-scoped view consumed by site
advisory optimization and orchestration readiness checks. It is derived from
raw/canonical source records and must keep quality/warning information from its
inputs.

The intermediate site settings layer reads raw records and produces:

```text
site_id
interval_start
interval_end
pv_power_kw
pv_energy_kwh
load_power_kw
load_energy_kwh
grid_import_power_kw
grid_import_energy_kwh
grid_export_power_kw
grid_export_energy_kwh
grid_voltage_health[]
data_quality
warnings[]
```

Grid connection prepared fields:

```text
grid_connection_id
enabled
export_allowed
effective_max_import_power_kw
effective_max_export_power_kw
planned_voltage_v
min_voltage_v
max_voltage_v
max_phase_imbalance_percent
```

Effective grid limits:

```text
if enabled=false:
  effective_max_import_power_kw = 0
  effective_max_export_power_kw = 0

if export_allowed=false:
  effective_max_export_power_kw = 0
```

Voltage health:

```text
average = (L1 + L2 + L3) / 3
max_deviation = max(abs(L1-average), abs(L2-average), abs(L3-average))
phase_imbalance_percent = max_deviation / average * 100
```

Island intent:

```text
if any enabled grid connection phase voltage is outside [min_voltage_v, max_voltage_v]:
  requires_island_transition = true

if phase_imbalance_percent > max_phase_imbalance_percent:
  requires_island_transition = true
```

This is a safety intent for the upper site layer. It is not a battery command.

## Metadata JSON Guidelines

Allowed examples:

```json
{
  "source_metric": "inverterYield",
  "source_unit": "kWh",
  "source_interval": "hour",
  "station_code": "NE=129469793"
}
```

```json
{
  "profile": "apoz",
  "period_seconds": 3600,
  "point_id": "869",
  "source_scale": -1
}
```

Forbidden:

```text
password
api_token
secret
cookie
xsrf_token
authorization header
database connection string
private key
```

## Naming Rules

- Use lowercase snake_case for `metric`, `source`, `quality`, and metadata keys.
- Use source-native ids for `instrument_id`.
- Use stable logical ids for `site_id`.
- Do not use display names as ids.
- Do not use battery `asset_id` as `site_id`.
- Do not mix multiple sites in one query result unless the API explicitly
  returns a multi-site collection.

## Validation Rules Before Writing

Reject a measurement when:

- required field is empty;
- `value` is NaN or infinite;
- `interval <= 0`;
- unit is not in the canonical unit catalog;
- metric is unknown and no mapping decision exists;
- timestamp cannot be converted to UTC;
- a secret-like field appears in `metadata_json`.

Warn but do not reject when:

- optional source name is missing;
- a source station has no data for a period;
- a source returns a known per-station error while other stations succeed;
- a value is stale but still useful for reporting.

## Open Decisions

1. Whether unavailable FusionSolar stations such as `failCode=20056` should
   create explicit `source_error` rows in `SiteMeasurementStore` or only logs.
2. Whether Deye site telemetry should be persisted into the universal
   `SiteMeasurementReading` table on every poll, or remain live snapshot only
   until SITE EMS needs history.
3. Whether price data should also be mirrored into `SiteMeasurementReading` or
   remain only in `price_series` / `price_series_points`.
4. Exact sign convention for `site_net_power` must be finalized before it is
   used for control.
