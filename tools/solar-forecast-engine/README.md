# Solar Forecast Sidecar

This folder contains a standalone forecasting engine that uses:

- `pvlib` as the open-source PV modeling core
- `Open-Meteo` as the free weather-data provider

## Purpose

The existing .NET module in `src/adapters/driven/BatteryEms.Adapters.OpenMeteo`
already exposes forecast storage and API wiring inside `bess-ems`.

This Python engine is the next step toward a truly separate forecasting
runtime. It can be called as a sidecar or subprocess and keeps the forecasting
math isolated from the main EMS host.

## Inputs

Use a config file compatible with:

- [`config/examples/solar-forecast.open-meteo.json`](../../config/examples/solar-forecast.open-meteo.json)

The engine reads the `OpenMeteoSolarForecast` section.

## Run

```powershell
python -m pip install -r tools/solar-forecast-engine/requirements.txt

python tools/solar-forecast-engine/pvlib_openmeteo_engine.py `
  --config config/examples/solar-forecast.open-meteo.json
```

## Output

The script prints a JSON forecast payload with:

- `asset_id`
- `source`
- `model`
- `horizon_start`
- `horizon_end`
- `time_step_seconds`
- `installed_dc_kw`
- `installed_ac_kw`
- `points[]`

Each point contains:

- timestamp
- forecast power
- irradiance
- ambient temperature
- wind speed
- cloud cover
