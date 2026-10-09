# Memory

## Deye Cloud real response check

Date checked: 2026-06-02.

Local `.env` credentials were loaded only into the current process. Secrets,
passwords, app secret, refresh token, and access token were not printed.

Token endpoint result:

```text
code: 1000000
msg: success
tokenType: bearer
expiresIn: 5183999
```

Important API shape finding:

- The real token response returns `accessToken`, `tokenType`, `expiresIn`,
  etc. at the root JSON level.
- It does not return the token under `data.accessToken`.

Station latest request:

```text
stationId: 62225431
```

Station latest response summary:

```text
code: 1000000
msg: success
success: true
lastUpdateTime: 2026-06-02 13:01:59Z
generationPower: 77790
consumptionPower: -18452
wirePower: -92421
chargePower: -1710
dischargePower: 600
batteryPower: -1110
batterySOC: 99.5
gridPower: null
purchasePower: null
irradiateIntensity: null
```

Important API shape finding:

- The real `/station/latest` response returns metrics at the root JSON level.
- It does not return station metrics under `data.dataList`.

EMS interpretation:

```text
batterySOC: 99.5 %
batteryPower: -1110 W
ActivePowerKw: -1.11 kW
```

With the EMS sign convention, `ActivePowerKw = -1.11` means the battery is
slightly charging.

Code status:

- Deye adapter was updated to support root-level token fields.
- Deye adapter was updated to support root-level station metrics.
- Deye station unit test was updated to match the real response shape.
- Focused Deye tests passed: 2/2.

## ENTSO-E / Ukrainian RDN price import

Date checked: 2026-06-03.

Local `.env` contains ENTSO-E configuration. The API token was treated as a
secret and was not printed.

Configuration keys now present:

```text
Bess__PriceSeriesSource = entso-e
Bess__EntsoeApiBaseUrl = https://web-api.tp.entsoe.eu/api
Bess__EntsoeApiToken = present, redacted
Bess__EntsoeDomainCode = 10Y1001C--000182
```

Live ENTSO-E A44 request result:

```text
status: 200 OK
domain: 10Y1001C--000182
market day: 2026-06-03 Europe/Kyiv
request UTC range: 2026-06-02 21:00Z -> 2026-06-03 21:00Z
currency_Unit.name: UAH
price_Measure_Unit.name: MWH
unit interpreted by EMS adapter: UAH/MWh
```

Important API shape finding:

- ENTSO-E returns the Ukrainian RDN day as sparse `Point` positions.
- Missing positions mean the previous price remains active, not that the price
  is missing.
- The live response for 2026-06-03 contained 20 points and omitted positions
  4, 5, 11, and 13.
- The adapter now expands sparse positions by carrying the last active price
  forward, producing a complete 24-hour series.

Expanded price sequence for 2026-06-03 RDN:

```text
1  -> 4596.42
2  -> 3300.00
3  -> 1980.00
4  -> 1980.00
5  -> 1980.00
6  -> 2127.00
7  -> 4100.00
8  -> 5500.00
9  -> 5900.00
10 -> 4000.00
11 -> 4000.00
12 -> 2789.00
13 -> 2789.00
14 -> 2867.00
15 -> 2789.00
16 -> 1789.00
17 -> 3005.16
18 -> 4414.00
19 -> 6490.00
20 -> 6900.00
21 -> 7600.00
22 -> 8300.00
23 -> 7988.00
24 -> 6440.00
```

This sequence was compared against the user-provided CSV
`price_DAM_IDM_06.2026 - Ціна_РДН.csv`.

Code changes:

- Added driven adapter `BatteryEms.Adapters.Entsoe`.
- `EntsoePriceSeriesSource` implements `IPriceSeriesSource`.
- It sends ENTSO-E A44 requests with:
  - `documentType=A44`
  - `in_Domain=<Bess__EntsoeDomainCode>`
  - `out_Domain=<Bess__EntsoeDomainCode>`
  - `periodStart` and `periodEnd` derived from `DateTimeOffset.UtcDateTime`
- For a local Kyiv market-day request of `2026-06-03 00:00 +03:00` ->
  `2026-06-04 00:00 +03:00`, the adapter sends:
  - `periodStart=202606022100`
  - `periodEnd=202606032100`
- It reads unit metadata from XML and stores `UAH/MWh` for the Ukrainian
  domain.
- It imports a successfully loaded series into `IPriceSeriesImportSink` when a
  sink is configured.

Persistence changes:

- Added migration `0006_price_series.sql`.
- Added tables:
  - `price_series`
  - `price_series_points`
- Added `DapperPriceSeriesStore`, implementing both:
  - `IPriceSeriesImportSink`
  - `IPriceSeriesSource`
- With `Bess:PersistenceConnectionString` configured, price series are stored in
  Postgres and survive process restarts.

How the main EMS system receives RDN data:

1. The operator/API caller sends an optimization request with a
   `price_series` reference.
2. `BatteryEms.Api` resolves that reference through `IPriceSeriesSource`.
3. In Host, when `Bess__PriceSeriesSource=entso-e`, `IPriceSeriesSource` is
   `EntsoePriceSeriesSource`, so the EMS fetches the requested horizon from
   ENTSO-E live.
4. If DB persistence is configured, the successful live series is also written
   through `IPriceSeriesImportSink` to `price_series` and
   `price_series_points`.
5. The resolved `PriceSeries` is passed into the schedule optimization or
   intraday reoptimization use case as part of the command.

Test status after the latest ENTSO-E/RDN work:

```text
BatteryEms.Adapters.Entsoe.Tests: 5/5 passed
BatteryEms.Adapters.Persistence.Tests: 13/13 passed
BatteryEms.Api.Tests: 48/48 passed
BatteryEms.ArchitectureTests: 29/29 passed
```

Notes:

- `dotnet-script` is not installed on this machine, so an ad-hoc C# live smoke
  via `dotnet script` could not be run.
- Live XML was checked directly with `Invoke-WebRequest`; adapter behavior is
  pinned by unit tests using the real sparse sequence from the CSV/API response.

## Local Postgres test database

Date checked: 2026-06-03.

The local Postgres server is reachable at:

```text
POSTGRES_HOST=127.0.0.1 / localhost
POSTGRES_PORT=5432
POSTGRES_DB=bessems_test
POSTGRES_USER=iaroslav
POSTGRES_PASSWORD=present, redacted
maintenance database=postgres
```

Important finding:

- The existing local `5432` Postgres is not a standard `postgres/postgres`
  instance.
- Roles `postgres`, `odoo`, and `bessems` were not usable.
- User-provided role `iaroslav` works with the password stored locally in
  `.env`.

Test database reset:

- Added `scripts/reset-test-database.ps1`.
- The script reads `POSTGRES_*` values from `.env`.
- It refuses to reset a database whose name does not contain `test`, unless
  explicitly overridden.
- For `bessems_test`, it drops and recreates the `public` schema before a test
  run.
- Reset confirmed that old EMS tables are removed, including:
  - `price_series`
  - `price_series_points`
  - `telemetry`
  - `commands`
  - `schedules`
  - migration journal `__schema_versions`

Persistence integration test result against the clean `bessems_test` DB:

```text
BatteryEms.Persistence.IntegrationTests: 48/48 passed
```

Full EMS solution test attempt:

Command:

```text
dotnet test BatteryEms.sln --no-restore
```

Result:

```text
FAILED
```

This was not caused by the new RDN/ENTSO-E persistence work. Main failure
categories:

- OR-Tools tests cannot load native `google-ortools-native`.
- NativeInterop integration tests cannot load/use the real native library.
- Modbus/MQTT/HIL integration tests need their simulators/brokers running.
- Some replay/config tests also failed in the broader environment-dependent
  run.

Confirmed green after the RDN/Postgres work:

```text
BatteryEms.Adapters.Entsoe.Tests: 5/5 passed
BatteryEms.Adapters.Persistence.Tests: 13/13 passed
BatteryEms.Persistence.IntegrationTests: 48/48 passed
BatteryEms.Api.Tests: 48/48 passed
BatteryEms.ArchitectureTests: 29/29 passed
```

## Full EMS retest after local SDK/Postgres fixes

Date checked: 2026-06-03.

Additional fixes made during the retest:

- `global.json` now requests valid SDK feature band `10.0.100` with
  `rollForward=latestFeature`; the previous `10.0.0` is not a valid .NET SDK
  version and blocks clean SDK resolution.
- `.dotnet-x64/` is ignored locally because it is a downloaded local SDK cache.
- `InMemorySnapshotStore` formats `snapshot-aged-...s` with invariant culture,
  so Ukrainian/Windows comma decimal formatting cannot change replay strings.
- `JsonFileConfigurationLoaderTests` now expects the current
  `asset.single-bess.json` values: `624 kWh`, `160 kW` charge/discharge,
  `13..99%` SOC range.
- `MultiAssetHostCompositionTests` explicitly clear `Bess:TelemetrySource` and
  `Bess:PriceSeriesSource` so local `.env` values for real Deye/ENTSO-E do not
  leak into architecture tests.

Clean DB reset before retest:

```text
scripts/reset-test-database.ps1
Host=127.0.0.1
Port=5432
Database=bessems_test
User=iaroslav
Result=public schema dropped and recreated
```

Focused retest results:

```text
BatteryEms.Application.Tests: 361/361 passed
BatteryEms.Infrastructure.Tests: 53/53 passed
BatteryEms.ArchitectureTests: 29/29 passed
```

Full solution retest command:

```text
.dotnet-x64/dotnet.exe test BatteryEms.sln --no-restore --verbosity minimal
```

Full solution result:

```text
FAILED because external services/native library are not available locally.
```

Confirmed passing projects in the full run included:

```text
BatteryEms.Domain.Tests: 343/343 passed
BatteryEms.Application.Tests: 361/361 passed
BatteryEms.Infrastructure.Tests: 53/53 passed
BatteryEms.Adapters.Entsoe.Tests: 5/5 passed
BatteryEms.Adapters.Persistence.Tests: 13/13 passed
BatteryEms.Persistence.IntegrationTests: 48/48 passed
BatteryEms.Adapters.Optimization.Tests: 97/97 passed
BatteryEms.Api.Tests: 48/48 passed
BatteryEms.ArchitectureTests: 29/29 passed after env isolation
```

Remaining failures are infrastructure prerequisites, not RDN/ENTSO-E logic:

- `BatteryEms.Modbus.IntegrationTests`: simulator not listening on
  `127.0.0.1:5020`.
- `BatteryEms.Mqtt.IntegrationTests`: broker not listening on
  `127.0.0.1:1883`.
- `BatteryEms.Hil.IntegrationTests`: HIL simulator not listening on
  `127.0.0.1:502`.
- `BatteryEms.NativeInterop.IntegrationTests`: missing
  `libbattery_control_core.so`; tests say to set `BESS_NATIVE_LIB_PATH` or build
  via `cmake -S native/battery_control_core -B build/native && cmake --build
  build/native`.

Docker status:

- Repo-supported paths for the missing services/native gates use Docker compose
  or Docker build targets (`make test-integration`, `make test-hil-modbus`,
  `make native-build`, `make test-native-interop`, `make test-native-parity`).
- Local `docker ps` currently fails with permission denied on
  `npipe:////./pipe/dockerDesktopLinuxEngine`.
- User `GRYSHIN-NB\ya.grishin` is not currently confirmed as having Docker
  engine access. Adding that user to Windows `docker-users` is a persistent
  host-level permission change and requires explicit approval.

## Docker deploy status

Date checked: 2026-06-04.

Docker access:

- Docker Desktop became reachable when commands were run outside the sandbox.
- `docker ps` initially returned an empty table, meaning the engine was
  accessible and no containers were running.

Build/deploy actions:

- Built `bess-ems-runtime:latest` successfully from the repo Dockerfile.
- The runtime image includes `/app/native/libbattery_control_core.so`; the
  Docker build ran the native CMake smoke test and `ldd` dependency check.
- Attempted to build `bess-field-sim:latest`, but Docker Hub/CloudFront pulls
  for `golang:1.26` repeatedly failed with TLS handshake timeout.
- Attempted to start compose Postgres, but the `postgres:16` image pull failed
  with the same CloudFront TLS handshake timeout.
- Deployed `bess-ems` as a standalone Docker container connected to the host
  PostgreSQL via `host.docker.internal:5432`, using local `.env` for Deye and
  ENTSO-E settings without printing secrets.

Running container:

```text
name=bess-ems
image=bess-ems-runtime:latest
status=running healthy
ports=0.0.0.0:8080->8080/tcp
database=host PostgreSQL bessems_test via host.docker.internal
```

Health/API verification:

```text
GET http://localhost:8080/health
200 {"status":"ok", ..., "components":{"database":"ok"}}
```

API snapshot from the running container:

```json
{
  "asset_id": "single-bess-1",
  "telemetry": {
    "soc_percent": 46.75,
    "soh_percent": 100,
    "active_power_kw": -160.2,
    "reactive_power_kvar": 0,
    "dc_voltage": 0,
    "dc_current": 0,
    "temperature_celsius": 25,
    "available": true,
    "fault_status": "OK"
  },
  "quality": {
    "flag": "Valid",
    "reason": "valid"
  },
  "last_command": {
    "mode": "Idle",
    "active_power_kw": 0,
    "reason": "no-active-commitment",
    "source": "Optimization"
  }
}
```

Log evidence:

- Deye Cloud token request returned HTTP 200.
- Deye station latest request returned HTTP 200.
- Telemetry ingestion source is `DeyeCloudTelemetrySource`.
- Control cycle worker started and emits idle commands because there is no
  active schedule/commitment.

## RDN/Postgres/optimizer fixes and Docker cache cleanup

Date checked: 2026-06-04.

Issues reproduced from the live RDN flow:

- PostgreSQL rejected RDN price-series writes when API requests used Kyiv local
  offsets such as `2026-06-04T00:00:00+03:00`: Npgsql only accepts UTC
  `DateTimeOffset` values for `timestamptz`.
- OR-Tools schedule optimization rejected ENTSO-E/RDN prices stored as
  `UAH/MWh` with `unsupported-price-unit:UAH/MWh`.

Code changes:

- `DapperPriceSeriesStore` now normalizes price-series horizon and point
  timestamps to UTC before writing or querying PostgreSQL.
- `OrToolsScheduleOptimizer` now accepts both `EUR/MWh` and `UAH/MWh`; objective
  component units are derived from the incoming price unit currency.
- Added regression tests for `UAH/MWh` optimization and non-UTC RDN price-series
  persistence.

Verification:

```text
BatteryEms.Adapters.Optimization.Tests: 98/98 passed
BatteryEms.Persistence.IntegrationTests --filter FullyQualifiedName~Price_series: 2/2 passed
```

Docker cache:

- Before cleanup, Docker build cache was `7.186GB`, all reclaimable.
- Ran `docker builder prune -af`.
- After cleanup, Docker build cache is `0B`.
- Running container was not removed and stayed healthy:

```text
name=bess-ems
image=bess-ems-runtime:latest
status=running healthy
ports=0.0.0.0:8080->8080/tcp
GET http://localhost:8080/health -> 200, database=ok
docker stats --no-stream -> CPU 0.11%, MEM 125.5MiB / 7.682GiB
```

## RDN tomorrow simulation after current-day remainder

Date checked: 2026-06-04.

Live EMS/Deye status used for the simulation:

```text
GET /battery/single-bess-1/status -> 200
telemetry timestamp=2026-06-04T10:07:28Z
soc_percent=67.5
active_power_kw=-151.33
last_command=Idle, reason=no-active-commitment
```

Important model caveat:

- The running host has `ScheduleSolver.InitialSocPercent` unset.
- With that unset, OR-Tools starts from the midpoint of the asset SOC band:
  `(13% + 99%) / 2 = 56%`.
- It does not automatically carry the terminal SOC from yesterday/today into a
  next day-ahead run unless the caller/config supplies that initial SOC.

Simulation inputs:

```text
asset=single-bess-1
capacity=624 kWh
max_charge=max_discharge=160 kW
soc_band=13..99 %
efficiency charge/discharge=0.95/0.95
market_day_tz=Europe/Kyiv (+03:00)
```

Current-day remainder from live SOC at 13:00 Kyiv:

```text
initial_soc=67.5%
end_soc_after_remaining_2026-06-04_plan=13%
```

Tomorrow RDN prices from ENTSO-E for 2026-06-05, unit `UAH/MWh`, 24 hourly
values:

```text
00 4600 | 01 3200 | 02 2300 | 03 2300 | 04 2300 | 05 2300
06 3600 | 07 3600 | 08 3600 | 09 1999 | 10 10   | 11 10
12 10   | 13 10   | 14 10   | 15 10   | 16 100  | 17 2300
18 6000 | 19 7590 | 20 8090 | 21 9892 | 22 10000| 23 7100
```

Tomorrow plan if carrying forward the simulated current-day terminal SOC
(`initial_soc=13%`):

```text
00 idle       0.000 kW  SOC 13.00 -> 13.00
01 idle       0.000 kW  SOC 13.00 -> 13.00
02 charge   -51.856 kW  SOC 13.00 -> 20.89
03 charge  -160.000 kW  SOC 20.89 -> 45.25
04 charge  -160.000 kW  SOC 45.25 -> 69.61
05 charge  -160.000 kW  SOC 69.61 -> 93.97
06 discharge 160.000 kW SOC 93.97 -> 66.98
07 discharge 160.000 kW SOC 66.98 -> 39.99
08 discharge 160.000 kW SOC 39.99 -> 13.00
09 idle       0.000 kW  SOC 13.00 -> 13.00
10 idle       0.000 kW  SOC 13.00 -> 13.00
11 idle       0.000 kW  SOC 13.00 -> 13.00
12 charge   -84.884 kW  SOC 13.00 -> 25.92
13 charge  -160.000 kW  SOC 25.92 -> 50.28
14 charge  -160.000 kW  SOC 50.28 -> 74.64
15 charge  -160.000 kW  SOC 74.64 -> 99.00
16 idle       0.000 kW  SOC 99.00 -> 99.00
17 idle       0.000 kW  SOC 99.00 -> 99.00
18 idle       0.000 kW  SOC 99.00 -> 99.00
19 discharge  29.808 kW SOC 99.00 -> 93.97
20 discharge 160.000 kW SOC 93.97 -> 66.98
21 discharge 160.000 kW SOC 66.98 -> 39.99
22 discharge 160.000 kW SOC 39.99 -> 13.00
23 idle       0.000 kW  SOC 13.00 -> 13.00
```

End SOC outcomes from the same run:

```text
system default start 56.0% -> tomorrow end 13.0%
live-now start 67.5%      -> tomorrow end 13.0%
carry-forward start 13.0% -> tomorrow end 13.0%
```

## Schedule economics reporting branch

Branch: `codex/rdn-stage-pnl-reporting`.

Design decision:

- Do not change the OR-Tools optimization engine/objective.
- Add PnL as a reporting layer over the produced schedule and the same price
  vector used for optimization.

Implemented shape:

- `ScheduleEconomicsCalculator` in Application calculates per-window:
  `price`, `target_power_kw`, `energy_mwh`, `cost`, `revenue`, `net_profit`,
  and `cumulative_net_profit`.
- Day-ahead optimize API response now includes optional `economics` when a
  schedule was produced and prices are available.
- Charge is modeled as cost; discharge is modeled as revenue. Net profit is
  `revenue - cost`.

Verification:

```text
BatteryEms.Application.Tests: 363/363 passed
BatteryEms.Api.Tests: 49/49 passed
BatteryEms.ArchitectureTests: 29/29 passed
```

## Nonlinear degradation and energy-loss reporting

Branch: `codex/rdn-stage-pnl-reporting`.

Implemented:

- OR-Tools degradation cost can now be power/C-rate weighted. When
  `NominalCRate` is configured, the LP uses convex piecewise-linear segment
  variables so marginal degradation cost rises with charge/discharge power.
- This gives the optimizer an economic reason to spread charge/discharge across
  equal-price hours instead of concentrating all energy into one hour.
- Host/env configuration supports:
  `Bess__ScheduleSolver__DegradationCostPerKwhThroughput`,
  `Bess__ScheduleSolver__DegradationNominalCRate`, and
  `Bess__ScheduleSolver__DegradationPiecewiseSegments`.
- `ScheduleEconomicsCalculator` and API `economics.steps[]` now include
  `battery_energy_delta_kwh` and `losses_kwh`; report-level economics includes
  `total_losses_kwh`.

Verification:

```text
BatteryEms.Adapters.Optimization.Tests: 100/100 passed
BatteryEms.Application.Tests: 363/363 passed
BatteryEms.Api.Tests: 49/49 passed
BatteryEms.ArchitectureTests: 29/29 passed
```

Simulation on 2026-06-05 RDN prices after enabling nonlinear degradation:

```text
raw live SOC=100%, modeled SOC clipped to asset max=99%
carry-forward SOC after remaining 2026-06-04 plan=13%
prices unit=UAH/MWh
```

Baseline without degradation:

```text
market cost=1228.92 UAH
market revenue=6431.36 UAH
market net=5202.45 UAH
degradation cost=0.00 UAH
net after degradation=5202.45 UAH
total losses=106.93 kWh
```

Nonlinear degradation, nominal C-rate 0.5C, rate 0.01:

```text
market net=5202.45 UAH
degradation cost=8.93 UAH
net after degradation=5193.52 UAH
total losses=106.93 kWh
logic changed: equal-price charging smoothed
night charge changed from -51.9/-160/-160/-160 kW to about -128/-131.9/-136/-136 kW
midday charge changed from -84.9/-160/-160/-160 kW to about -88/-92.9/-96/-96/-96/-96 kW
```

Nonlinear degradation, nominal C-rate 0.5C, rate 1.00:

```text
market cost=674.53 UAH
market revenue=5625.98 UAH
market net=4951.45 UAH
degradation cost=516.94 UAH
net after degradation=4434.51 UAH
total losses=83.16 kWh
logic changed strongly: lower peak powers and less cycling at marginal spreads
```

## Read-only solar/site telemetry integration

Branch: `codex/rdn-stage-pnl-reporting`.

Constraint kept: PV/site integration does not touch the battery calculation
engine. No changes were made to `ControlCycleUseCase`,
`ScheduleOptimizationRequest`, `BatteryTelemetry`, or the OR-Tools schedule
model for this slice.

Implemented:

- Added separate Application read-model:
  `SiteTelemetry`, `ISiteTelemetryStore`, `InMemorySiteTelemetryStore`.
- Added `ISiteStatusQuery` and read-only API endpoint:
  `GET /site/{assetId}/status`.
- Deye Cloud adapter now maps station/site fields into the separate site
  snapshot:
  `generationPower`/`totalSolarPower` -> `pv_power_kw`,
  `consumptionPower`/`totalLoadPower` -> `load_power_kw`,
  `gridPower`/`totalGridPower` -> `grid_power_kw`,
  `irradiateIntensity` -> `irradiance_w_per_square_meter`.
- Missing site fields are preserved as `null`, not coerced to zero.
- Battery telemetry emitted by Deye remains unchanged and continues to feed
  the existing snapshot/control path only.

Verification:

```text
BatteryEms.Application.Tests focused SiteTelemetry: 3/3 passed
BatteryEms.Adapters.DeyeCloud.Tests: 2/2 passed
BatteryEms.Api.Tests focused SiteStatus: 2/2 passed
BatteryEms.Api.Tests: 51/51 passed
BatteryEms.Application.Tests: 366/366 passed
BatteryEms.ArchitectureTests: 29/29 passed
BatteryEms.Adapters.Optimization.Tests: 100/100 passed with .\.dotnet-x64\dotnet.exe
```

Standalone FusionSolar live poll on 2026-06-04:

```text
env source=.env
endpoint=/getKpiStationHour
target_date_utc=2026-06-04
polled_at_utc=2026-06-04T16:57:36Z
station_count=4

NE=133657926 -> success=false, failCode=20056, data_count=0
NE=158463133 -> success=false, failCode=20056, data_count=0
NE=134735482 -> success=false, failCode=20056, data_count=0
NE=129469793 -> success=true, failCode=0, data_count=19
  latest_collect_utc=2026-06-04T15:00:00Z
  latest_collect_kyiv=2026-06-04 18:00:00 +03:00
  inverterYield=10.41
  mapped SiteTelemetry.PvPowerKw=10.41 average kW for that hourly interval
```

## Site-level optimization foundation

Branch: `codex/site-level-optimization-layer`.

Implemented first safe foundation slice:

- Added `BatteryEms.Application.Site`.
- Added site model records:
  `SiteDescriptor`, `GridConnection`, `SiteSourceRef`,
  `SiteInstrumentSet`, `SiteInstrument`.
- Added `ISiteRegistry` and `InMemorySiteRegistry`.
- Added `SiteConfigurationValidator` with fail-closed checks:
  required site metadata, at least one grid connection, at least one enabled
  grid connection, valid planned/min/max voltage, phase imbalance limit,
  import/export limits, and instrument/source compatibility.
- Battery engine untouched: no changes to `ControlCycleUseCase`,
  `BatteryTelemetry`, `ISnapshotStore`, `ScheduleOptimizationRequest`, or
  OR-Tools optimization model.

Verification:

```text
Focused Site Application tests: 10/10 passed
BatteryEms.Application.Tests: 376/376 passed
```

## ASKUE site consumption module

Branch: `codex/site-level-optimization-layer`.

Source reviewed:

```text
C:\Users\ya.grishin\Desktop\askue.txt
ASKUE base API: http://askue.net/api/askue/v1/json/
points endpoint: POST /points
profiles:
  1 -> apoz
  2 -> aneg
  3 -> ppoz
  4 -> pneg
```

Implemented:

- Added Application site consumption model:
  `SiteConsumptionReading`, `SiteConsumptionQuery`, `ISiteConsumptionStore`.
- Added `InMemorySiteConsumptionStore`.
- Added Postgres-backed `DapperSiteConsumptionStore`.
- Added migration:
  `0007_site_consumption_readings.sql`.
- Added driven adapter project:
  `BatteryEms.Adapters.Askue`.
- ASKUE collector:
  - uses Basic Authentication
  - reads `/points`
  - optionally filters by configured point IDs
  - reads point profiles 1/2/3/4
  - aggregates values into configured intervals
  - stores non-empty readings through `ISiteConsumptionStore`
  - `scripts/backfill-askue-history.ps1` now also supports browser-session
    cookies via `Askue__Cookie` / `-CookieHeader` and sends browser-like
    `Origin`, `Referer`, and `X-Requested-With` headers for web-session-based
    ASKUE access behind Cloudflare
- Host wiring:
  `Bess:ConsumptionSource=askue`.

Configuration keys:

```text
Bess__ConsumptionSource=askue
Askue__SiteId=...
Askue__BaseUrl=http://askue.net/api/askue/v1/json/
Askue__Username=...
Askue__Password=...
Askue__PointIds=optional comma-separated point ids
Askue__PeriodSeconds=3600
Askue__PollIntervalSeconds=3600
Askue__DaysBack=1
```

Battery engine untouched: no changes to `ControlCycleUseCase`,
`BatteryTelemetry`, `ISnapshotStore`, `ScheduleOptimizationRequest`, or
OR-Tools optimization model.

Verification:

```text
BatteryEms.Adapters.Askue.Tests: 1/1 passed
InMemorySiteConsumptionStore focused tests: 2/2 passed
BatteryEms.Host build: 0 warnings, 0 errors
Persistence migration focused tests: 13/13 passed
BatteryEms.ArchitectureTests: 29/29 passed
BatteryEms.Application.Tests: 378/378 passed
BatteryEms.Adapters.Optimization.Tests: 100/100 passed with .\.dotnet-x64\dotnet.exe
```

Real Deye Cloud poll on 2026-06-04:

```text
endpoint=/station/latest
station_id=62225431
response_code=1000000
response_msg=success
poll samples at UTC:
  2026-06-04T16:32:50Z
  2026-06-04T16:32:55Z
  2026-06-04T16:33:01Z
server lastUpdateTime=2026-06-04T16:27:49Z / 2026-06-04 19:27:49 +03:00 Kyiv
dataList count=0; telemetry values were returned at root JSON level
generationPower=5880 W -> pv_power_kw=5.88
consumptionPower=394 W -> load_power_kw=0.394
wirePower=-4885 W
batteryPower=110 W -> battery_active_power_kw=0.11
dischargePower=110 W
chargePower=null
batterySOC=99.5 %
gridPower=null
irradiateIntensity=null
```

## FusionSolar site telemetry adapter

Branch: `codex/rdn-stage-pnl-reporting`.

Implemented:

- Added `BatteryEms.Adapters.FusionSolar`.
- Adapter authenticates against Huawei FusionSolar thirdData API:
  `/login`, reads `XSRF-TOKEN`, then calls `/getKpiStationHour`.
- `inverterYield` from hourly KPI records is mapped into read-only
  `SiteTelemetry.PvPowerKw` as average power for that hour
  (`kWh per hour -> kW average`).
- Adapter updates only `ISiteTelemetryStore`; it does not write
  `BatteryTelemetry`, `ISnapshotStore`, schedules, or optimizer inputs.
- Host wiring:
  `Bess:SiteTelemetrySource=fusionsolar`
  and `FusionSolar:User`, `FusionSolar:Password`,
  `FusionSolar:StationCodes`, optional `FusionSolar:AssetId`,
  `FusionSolar:PollIntervalSeconds`.
- Host exposes the existing read-only endpoint:
  `GET /site/{assetId}/status`.

Verification:

```text
BatteryEms.Adapters.FusionSolar.Tests: 1/1 passed
BatteryEms.ArchitectureTests: 29/29 passed
BatteryEms.Host build: 0 warnings, 0 errors
BatteryEms.Adapters.Optimization.Tests: 100/100 passed with .\.dotnet-x64\dotnet.exe
```

## Current handoff

Date: 2026-06-05.

Current focus:

- Site-level PV forecast integration where the predictor is a separate runtime
  concern and the site/planning layer consumes persisted forecast data from DB.

What was implemented in this pass:

- Added site PV profile model and store:
  `src/hexagon/BatteryEms.Application/Site/SitePvProfile.cs`
- Added Postgres persistence for PV profiles:
  `src/adapters/driven/BatteryEms.Adapters.Persistence/DapperSitePvProfileStore.cs`
- Added Postgres persistence for latest solar forecast snapshot:
  `src/adapters/driven/BatteryEms.Adapters.Persistence/DapperSolarForecastStore.cs`
- Added DB migrations:
  - `0009_site_pv_profiles.sql`
  - `0010_solar_forecasts.sql`
- Wired persistence registrations so Host/API can resolve:
  - `ISitePvProfileStore`
  - `ISolarForecastStore`
- Extended Open-Meteo forecast provider contract to support per-profile
  requests, not just one static configured asset.
- Updated `OpenMeteoSolarForecastHostedService` so it:
  - reads `ISitePvProfileStore.ListEnabledAsync()`
  - refreshes forecasts for every enabled PV profile
  - stores results through `ISolarForecastStore`
  - falls back to old single-asset mode only when no PV profiles exist
    and `OpenMeteoSolarForecast:AssetId` is configured
- Removed the old host restriction that forced solar forecasting to exactly one
  configured asset when explicit PV-profile routing is available.
- Added API endpoints for site PV profiles:
  - `GET /site/{siteId}/pv-profiles`
  - `PUT /site/{siteId}/pv-profiles/{pvSystemId}`
- Mapped the new endpoint set in both:
  - `src/adapters/driving/BatteryEms.Api/Program.cs`
  - `src/host/BatteryEms.Host/BessHostBuilder.cs`

Intended runtime flow now:

1. Site creates or updates one or more PV profiles.
2. Forecast hosted service wakes up on schedule.
3. It loads all enabled PV profiles.
4. It computes a forecast per profile using Open-Meteo + selected backend
   (`pvlib_sidecar` or `embedded`).
5. It writes the latest forecast snapshot to DB.
6. Site-side planning later reads persisted forecast data and combines it with:
   - load / site consumption
   - price series
   - battery state
   - grid constraints

Important files for the next person:

- Forecast runtime:
  - `src/adapters/driven/BatteryEms.Adapters.OpenMeteo/OpenMeteoSolarForecastHostedService.cs`
  - `src/adapters/driven/BatteryEms.Adapters.OpenMeteo/OpenMeteoSolarForecastSource.cs`
  - `src/adapters/driven/BatteryEms.Adapters.OpenMeteo/PvlibSidecarSolarForecastSource.cs`
  - `src/adapters/driven/BatteryEms.Adapters.OpenMeteo/OpenMeteoSolarForecastOptions.cs`
- Persistence:
  - `src/adapters/driven/BatteryEms.Adapters.Persistence/DapperSitePvProfileStore.cs`
  - `src/adapters/driven/BatteryEms.Adapters.Persistence/DapperSolarForecastStore.cs`
  - `src/adapters/driven/BatteryEms.Adapters.Persistence/Migrations/RunOnce/0009_site_pv_profiles.sql`
  - `src/adapters/driven/BatteryEms.Adapters.Persistence/Migrations/RunOnce/0010_solar_forecasts.sql`
- API:
  - `src/adapters/driving/BatteryEms.Api/Endpoints/SitePvProfileEndpoints.cs`
  - `src/adapters/driving/BatteryEms.Api/Endpoints/SolarForecastEndpoints.cs`
- Docs:
  - `docs/user/solar-forecast.md`

Tests added/updated:

- `tests/hexagon/BatteryEms.Application.Tests/InMemorySitePvProfileStoreTests.cs`
- `tests/adapters/driven/BatteryEms.Adapters.OpenMeteo.Tests/OpenMeteoSolarForecastHostedServiceTests.cs`
- `tests/adapters/driving/BatteryEms.Api.Tests/SiteStatusEndpointTests.cs`
- `tests/integration/BatteryEms.Persistence.IntegrationTests/PersistenceRoundtripTests.cs`
- `tests/adapters/driven/BatteryEms.Adapters.Persistence.Tests/MigrationResourceSetTests.cs`

Current blocker:

- Local `dotnet build/test` verification is still unreliable in this machine
  context.
- Using the bundled `.dotnet-x64` SDK `10.0.300`, builds fail with
  `Build FAILED` and `0 Error(s)`.
- Diagnostic output still points to MSBuild project-reference evaluation
  (`_GetProjectReferenceTargetFrameworkProperties`) rather than normal C#
  compile diagnostics.
- Setting `MSBuildEnableWorkloadResolver=false` did not resolve it.

Practical implication:

- The new solar/site code is wired logically and covered with tests in repo,
  but full compile/run confirmation is still blocked by the local SDK/MSBuild
  environment.

Recommended next step:

- Stabilize local .NET build execution first.
- Then run focused verification in this order:
  1. `BatteryEms.Adapters.OpenMeteo.Tests`
  2. `BatteryEms.Adapters.Persistence.Tests`
  3. `BatteryEms.Persistence.IntegrationTests`
  4. `BatteryEms.Api.Tests`
- After that, implement the next business slice:
  site-side planning use case that reads PV forecast + load + prices +
  battery state and produces the operating plan for today/next day.

## FusionSolar per-station polling handoff

Date: 2026-06-05.

User request:

- Fix FusionSolar polling so a failed station does not stop collection from
  the remaining stations.

Observed live FusionSolar API behavior:

```text
env source=.env
endpoint=/getKpiStationHour
station_count=4
polled_at=2026-06-05T18:19:14+03:00

NE=133657926 -> success=false, failCode=20056, data_count=0
NE=158463133 -> success=true,  failCode=0, data_count=17,
  latest_collect_utc=2026-06-05T13:00:00Z, inverterYield=3.64
NE=134735482 -> success=true,  failCode=0, data_count=17,
  latest_collect_utc=2026-06-05T13:00:00Z, inverterYield=3.2
NE=129469793 -> success=true,  failCode=0, data_count=17,
  latest_collect_utc=2026-06-05T13:00:00Z, inverterYield=3.6
```

Saved protocol:

- `tmp/fusionsolar-live-latest.json`

Implemented:

- `src/adapters/driven/BatteryEms.Adapters.FusionSolar/FusionSolarSiteTelemetrySource.cs`
  now handles failures per station inside `PollOnceAsync`.
- The adapter still authenticates once, then polls station codes
  sequentially.
- If one station returns a FusionSolar API error such as `failCode=20056`,
  or has an HTTP/JSON/timeout problem, the adapter logs a warning and
  continues to the next station.
- Cancellation requested by the caller is still rethrown.
- The main battery EMS engine was not touched.

Tests:

- Added regression coverage in
  `tests/adapters/driven/BatteryEms.Adapters.FusionSolar.Tests/FusionSolarSiteTelemetrySourceTests.cs`:
  one station returns `20056`, the next station succeeds, and the poll returns
  the successful update instead of throwing.
- Added opt-in live scratch test:
  `tests/adapters/driven/BatteryEms.Adapters.FusionSolar.Tests/FusionSolarLiveScratchTests.cs`.
  It only runs when `BESS_RUN_LIVE_FUSIONSOLAR_TESTS=1`.

Verification:

```text
FusionSolar mock/unit tests:
Passed 2/2

FusionSolar live scratch test with BESS_RUN_LIVE_FUSIONSOLAR_TESTS=1:
Passed 1/1
```

Current interpretation:

- FusionSolar credentials and login work.
- `.env` currently contains 4 station codes.
- FusionSolar currently returns usable hourly KPI data for 3 of 4 stations.
- `NE=133657926` is still unavailable from the server side with
  `failCode=20056`.
- After the fix, the site telemetry adapter can still update the 3 available
  stations instead of losing the whole polling cycle.

Recommended next step:

- Investigate `NE=133657926` in FusionSolar/Huawei portal:
  permissions, station ownership, API access rights, or whether the station
  has hourly KPI data enabled.
- Decide whether unavailable station telemetry should be persisted as a
  separate site measurement with error status, or only logged for now.

## Data attributes and internal orchestrator planning handoff

Date: 2026-06-06.

User intent:

- Standardize how collected data is stored so ASKUE, Deye Cloud,
  FusionSolar, ENTSO-E/RDN and future sources use the same structure,
  units, time semantics and quality attributes.
- Design an internal orchestrator for `bess-ems` that decides which modules
  run, in which order, with PostgreSQL state, idempotency, data freshness,
  agent participation and future command approval gates.

Created docs:

- `data_atribute.md`
- `docs/orchestrator-plan.md`

`data_atribute.md` defines:

- canonical `SiteMeasurementReading` shape:
  `site_id`, `source`, `instrument_type`, `instrument_id`,
  `instrument_name`, `timestamp`, `interval`, `metric`, `value`, `unit`,
  `quality`, optional `group_parent_id`, `scale`, `metadata_json`.
- canonical units:
  `kW`, `kWh`, `kvar`, `kvarh`, `V`, `A`, `UAH/MWh`, `UAH/kWh`, `UAH`,
  `%`, `degC`, `W/m2`.
- metric names for PV, load, grid, voltage, battery/site prepared values,
  and prices.
- conversion rules:
  `W -> kW`, `Wh -> kWh`, `MWh -> kWh`, `UAH/MWh -> UAH/kWh`,
  `energy_kwh / interval_hours -> average_power_kw`.
- quality values:
  `measured`, `derived`, `estimated`, `stale`, `missing`, `source_error`,
  `invalid`.
- source mapping rules:
  - ASKUE profiles:
    `apoz -> active_energy_import`,
    `aneg -> active_energy_export`,
    `ppoz -> reactive_energy_import`,
    `pneg -> reactive_energy_export`.
  - FusionSolar:
    `inverterYield` is hourly energy and should map to `pv_energy`
    or `inverter_yield`; average `pv_power` may be derived for 1h
    intervals.
  - Deye:
    site/PV/load fields map to site measurements/telemetry; battery
    telemetry remains in the battery path.
  - ENTSO-E/RDN:
    store day-ahead market prices as `market_price_day_ahead` in
    `UAH/MWh`; sparse point positions are expanded by carrying the
    previous price forward.
- prepared site settings output:
  PV/load/grid power and energy, grid voltage health, data quality and
  warnings.
- forbidden metadata/log content:
  passwords, tokens, cookies, connection strings, private keys and other
  secrets.

`docs/orchestrator-plan.md` defines:

- an internal orchestrator inside `bess-ems`, not a separate service.
- mixed triggers:
  daily RDN price import, ASKUE 15/60 min polling, frequent Deye/FusionSolar
  polling, source events, manual starts, retries and future approvals.
- PostgreSQL as the source of truth for:
  `orchestration_runs`, `orchestration_steps`, `orchestration_locks`,
  `orchestration_data_balances`.
- idempotency keys for runs and steps to prevent duplicate work.
- data roles:
  `critical`, `blocking_safety`, `advisory`, `informational`, `optional`.
- data statuses:
  `unknown`, `ready`, `partial`, `missing`, `stale`, `source_error`,
  `invalid`, `retrying`, `blocked`, `degraded`.
- freshness policy defaults for RDN, ASKUE, Deye, FusionSolar, prepared
  site settings, battery optimization and agent proposals.
- dependency graphs for:
  day-ahead run,
  near-real-time source refresh,
  future control path.
- retry behavior:
  critical data blocks dependent workflows;
  advisory/informational data retries and marks reports degraded;
  FusionSolar per-station failures may yield a partial module result.
- agent involvement from the beginning:
  bounded no-secret context, proposal/audit records, no direct command
  dispatch.
- future command path:
  `advisory_ready -> proposal_created -> validation_passed ->
  awaiting_approval -> approved -> command_queued -> command_dispatched ->
  command_confirmed`.

Architecture alignment added to the orchestrator plan:

- Follows hexagonal dependency rule:
  Worker/API drive orchestration;
  Application orchestration decides workflow;
  driven adapters collect/persist facts.
- New Application namespace should be
  `BatteryEms.Application.Orchestration`; do not split into a new csproj
  unless ADR 0011 split triggers are met.
- Persistence must use existing project style:
  Dapper/Npgsql repositories and forward-only DbUp SQL migrations under
  `Migrations/RunOnce`.
- Source adapters must not contain orchestration business decisions.
- Optimizers still do not write to devices.
- Control commands remain gated by safety, state machine, limiter and
  future explicit approval.
- Site orchestration is keyed by `site_id`; battery command ownership remains
  keyed by `asset_id`.
- Safe fallback/fail-closed behavior is required for critical/safety
  uncertainty.
- Edge/hard real-time responsibilities remain outside Docker EMS according
  to ADR 0008.

Orchestrator first implementation slice is now present:

- Application orchestration models, ports, readiness policy, in-memory stores
  and default use case live under `BatteryEms.Application.Orchestration`.
- API composition registers the orchestration use case and in-memory stores.
- Persistence adapter has Dapper stores for orchestration runs, steps, locks
  and data-balance statuses.
- DbUp migration `0011_orchestration.sql` creates:
  `orchestration_runs`, `orchestration_steps`, `orchestration_locks`,
  `orchestration_data_balances`.
- Persistence registration maps orchestration ports to Dapper stores when the
  Postgres adapter is enabled.
- Main battery optimization/control engine remains untouched by this slice.

Suggested implementation order:

1. Application records and in-memory stores:
   `OrchestrationRun`, `OrchestrationStep`, `DataBalanceStatus`,
   `OrchestrationModuleDefinition`, `OrchestrationTrigger`.
2. Application interfaces:
   `IOrchestrationRunStore`, `IOrchestrationLockStore`,
   `IDataBalanceStore`, `IOrchestrationUseCase`,
   `IOrchestrationModule`.
3. Application unit tests for use case, readiness policy, idempotency,
   dependencies and agent proposal gates.
4. PostgreSQL migrations and Dapper stores.
5. Persistence tests.
6. API/Worker wiring.
7. Architecture boundary tests:
   Application.Orchestration must not reference adapter implementations;
   driven source adapters must not reference orchestration use cases;
   first slice must not reference `IBatteryCommandSink` directly;
   agent proposal code must not reference secret-bearing option types.
8. Narrow opt-in live/scratch tests only after deterministic tests pass.

Important status:

- `docs/orchestrator-plan.md` and `data_atribute.md` are new/untracked files.
- No real secrets were written into either document.
- The main battery engine was not changed by these planning/docs steps.

Verification after orchestration slice:

- `OrchestrationUseCaseTests`: 5/5 passed.
- `MigrationResourceSetTests`: 4/4 passed.
- `OrchestrationPersistenceRoundtripTests`: 3/3 passed against local
  test PostgreSQL at `127.0.0.1:55432`, database `bessems_test`.
- Full Docker integration compose was attempted, but Docker could not pull
  required images (`golang:1.26`, `postgres:16`) because registry downloads
  timed out during TLS handshake. This is an external Docker pull/network
  blocker, not an orchestration code failure.

## Architecture consistency pass

Date: 2026-06-08.

Documents aligned:

- `spec/architecture.md`
- `spec/lastenheft.md`
- `docs/site-level-optimization-plan.md`
- `docs/orchestrator-plan.md`
- `data_atribute.md`

Unified project rules:

- `BatteryEms.Application` remains one project with namespaces for Site and
  Orchestration, per ADR 0011.
- Persistence wording is Dapper/Npgsql + DbUp/PostgreSQL, per ADR 0001. EF
  Core is not a runtime persistence or migration path.
- Shared worker / per-asset fan-out remains the default, per ADR 0007.
- Edge/hardware boundary remains outside the Docker EMS, per ADR 0008.
- `site_id` coordinates site-level data and workflows.
- `asset_id` owns battery telemetry, schedules and command authority.
- `instrument_id` is a source-native id, not a replacement for either
  `site_id` or `asset_id`.
- Source adapters collect and normalize facts; Application decides readiness,
  policy, orchestration and advisory logic.
- Site EMS starts as reporting/advisory; no direct battery command dispatch is
  introduced by Site, Orchestration or Agent layers.
- Canonical prepared site output is named `SitePreparedSettings`.
- `SiteMeasurementReading` is the common vocabulary for site source
  measurements; specialized tables such as `price_series` may stay canonical
  when they model the domain more precisely.

## Module compliance review

Date: 2026-06-08.

Review artifact:

- `docs/module-compliance-review.md`

Battery EMS core was not changed.

Non-core alignment fixes made:

- `SiteMeasurementReading.Value` now supports `null` only for status-only
  qualities: `missing`, `source_error`, `invalid`.
- New migration `0012_site_measurement_status_rows.sql` drops NOT NULL from
  `site_measurements.value`.
- `DefaultSiteSettingsPreparationUseCase` accepts canonical
  `inverter_yield` and legacy/raw `inverterYield`.
- Open-Meteo helper methods were marked static to satisfy analyzer gates.

Important follow-up:

- ASKUE, FusionSolar and Open-Meteo currently contain their own polling hosted
  services. This is acceptable as temporary collector behavior, but the
  documented target is that Worker/Orchestration owns module timing and source
  adapters only collect facts.

Verification:

- Site-focused Application tests: 8/8 passed.
- Persistence migration resource tests: 4/4 passed.
- Architecture tests: 29/29 passed.
- Focused Postgres site measurement status-row roundtrip: 1/1 passed.

## ASKUE unit correction

Date: 2026-06-10.

Problem found:

- The site-balance test pinned physically wrong Khlibzavod-5 values such as
  `generation export = 9713.16 kWh` for ASKUE meter 929.
- That value is the plain sum of 30-minute ASKUE interval profile values after
  `value_multiplier=400`; it did not account for interval duration.
- ASKUE adapter already preserves source profile values and
  `interval_seconds`; `/points.scale` is stored in metadata and is not applied
  as a physical unit conversion.
- `scripts/backfill-askue-history.ps1` now separates three semantics:
  - raw `/profile` values for audit (`raw_apoz`, `raw_aneg`, `raw_ppoz`,
    `raw_pneg`)
  - web-monitor display values as `source_value * value_multiplier`
    (`site_active_import_kw`, `site_active_export_kw`,
    `site_reactive_import_kvar`, `site_reactive_export_kvar`)
  - canonical interval energy as
    `source_value * value_multiplier * interval_seconds / 3600`

Rule now used:

- Persisted `site_consumption_readings` remain auditable source values.
- `DefaultSiteBalanceUseCase` converts persisted ASKUE interval values to
  canonical energy before passing them into `SiteBalanceCalculator`:
  `source_value * value_multiplier * interval_seconds / 3600`.
- `scripts/test-site-balance.ps1` uses the same rule for live ASKUE control
  runs: every profile point is converted with `item.step / 3600`; when `step`
  is absent it falls back to the script `-PeriodSeconds` parameter.
- If `interval_seconds` is missing, the fallback remains
  `source_value * value_multiplier` for backward compatibility.
- `/points.scale` remains an orientation/audit field only and must not replace
  `value_multiplier`.

## ASKUE web-session access

Date: 2026-07-20.

Problem found:

- ASKUE web monitoring requests in browser HAR no longer behaved like simple
  unauthenticated POSTs.
- `GET /show/content.html` still returned `200`, but AJAX requests such as
  `POST /meter/929.html` and `POST /meter/871.html` returned `403` with a
  Cloudflare `Just a moment...` challenge page.
- The recorded HAR showed browser-style headers on `/meter/*.html` requests
  and confirmed that web monitoring now depends on a valid browser session.

Current rule:

- Copying ASKUE web-monitor behavior requires a valid browser session, not only
  Basic Auth.
- Runtime cookie support is now wired into
  `scripts/backfill-askue-history.ps1` through `Askue__Cookie` in `.env` or
  `-CookieHeader` on the command line.
- The cookie string may include session cookies such as `askue` and
  Cloudflare cookies such as `cf_clearance`.
- These cookies are runtime access credentials only. They must never be stored
  in persisted measurements, reports, logs, or memory snapshots.

Verification:

- On 2026-07-20, after setting `Askue__Cookie`, the live one-day KGU export
  for 2026-05-01 completed successfully and saved
  `tmp/askue-kgu-cookie-test.csv`.

Result for the previously pinned 30-minute 929 sample:

- Gas cogeneration export changes from `9713.16 kWh` to `4856.58 kWh`.
- Gas cogeneration auxiliary consumption changes from `110.64 kWh` to
  `55.32 kWh`.
- Gas cogeneration net generation changes from `9602.52 kWh` to
  `4801.26 kWh`.

Verification:

- Application focused tests (`SiteBalance|SiteSettingsPreparation|SiteConsumption`): 14/14 passed.
- ASKUE collector tests without live credentials test: 2/2 passed.
- Persistence migration resource tests: 4/4 passed.

## Current FusionSolar production handoff

Date checked: 2026-07-17.

This section supersedes the older FusionSolar handoffs that describe
`getKpiStationHour`, per-station login cycles, three configured stations, or
partial `20056/407` results.

Production location:

```text
server=jar@10.10.70.66
project=/home/jar/apps/bess-ems
compose=/home/jar/apps/bess-ems/deploy/compose.yml
container=deploy_bess-ems_1
image=bess-ems-runtime:latest
operator=http://10.10.70.66:8080/operator/
```

Do not store the SSH password, FusionSolar password, XSRF token, or other
secrets in this file. Runtime secrets remain in the server `.env`.

Current FusionSolar design:

- `getKpiStationHour` is no longer used by the hosted collector.
- The collector uses the official monitoring endpoint
  `/thirdData/getStationRealKpi`.
- All configured station codes are sent in one request every 300 seconds.
- The response field `dataItemMap.active_power` is interpreted as current PV
  power in kW.
- Duplicate records for a station are reduced to the newest `collectTime`.
- Valid station powers are summed into the single EMS site asset telemetry.
- Requests are serialized.
- One XSRF token is reused for 29 minutes; Huawei documents a 30-minute token
  lifetime and each new login invalidates the previous token.
- The site telemetry snapshot max age is 40 minutes.

Production station list:

```text
NE=133657926
NE=158463133
NE=134735482
NE=129469793
```

The fourth station `NE=133657926` was initially missing from the server
`.env`. It was added and the EMS container was recreated without rebuilding
sidecars.

Latest verified production result:

```text
health=ok
database=ok
FusionSolar__PollIntervalSeconds=300
FusionSolar real-time plant cycle: Updated stations=4
407 ACCESS_FREQUENCY_IS_TOO_HIGH on new endpoint: not observed
operator site status quality=Valid
pv_power_kw=4.22
load_power_kw=33.739
container restart count after deployment=0
disk free=85 GB
disk used=25%
```

Verification:

```text
BatteryEms.Adapters.FusionSolar.Tests: 7/7 passed
Docker image build: succeeded
GET /health: status=ok, database=ok
GET /site/single-bess-1/status: telemetry present, quality=Valid
```

Operational notes:

- The server uses standalone Compose v1: use `docker-compose`, not
  `docker compose`.
- Recreate only the application with:
  `docker-compose -f deploy/compose.yml up -d --no-deps --force-recreate bess-ems`.
- After a successful health check, `docker image prune -f` is used only for
  dangling images; do not remove tagged images or volumes.
- If the real-time endpoint begins returning a limit error, inspect the latest
  cycle before changing timing. Do not reintroduce per-station login cycles.
- The documented secondary option is
  `/rest/openapi/pvms/nbi/v1/plant/power-flow`, but it was not needed because
  `getStationRealKpi` succeeded for all four stations.
