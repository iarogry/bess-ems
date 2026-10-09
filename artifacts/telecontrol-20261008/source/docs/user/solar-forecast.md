# Solar Forecast Module

The initial solar forecasting module is an advisory engine that keeps the
existing battery core unchanged.

## What It Does

- Pulls free weather forecasts from Open-Meteo.
- Uses Open-Meteo global tilted irradiance (GTI) for the configured panel tilt
  and azimuth.
- Converts weather data into a medium-accuracy PV generation forecast.
- Stores the latest forecast in the forecast store and exposes it over HTTP.

## Open-Source Basis

- Weather source: Open-Meteo free forecast API.
- Primary runtime mode: separate `pvlib` sidecar process.
- Fallback/runtime compatibility mode: embedded GTI/PVWatts-style calculation
  inside the .NET adapter.
- The application/API contract stays stable across both backends.

An initial standalone sidecar prototype now lives in
[`tools/solar-forecast-engine`](../../tools/solar-forecast-engine/README.md).

## Current Scope

- Intended for preliminary dispatch and operator visibility.
- Supports 15-minute or hourly output.
- Designed for one configured PV site per host instance in the first slice.
- Persists site PV profile metadata in `site_pv_profiles`.
- Persists the latest forecast snapshot in `solar_forecasts` and
  `solar_forecast_points`, so site-side planning can consume forecast data
  later without calling the predictor again.
- Does not yet perform historical self-learning, shading maps, snow/soiling
  correction, or multi-provider ensemble blending.

## Site-Side Workflow

For a real station, the intended flow is:

1. Register a PV profile for the site with geometry and installed power.
2. Refresh the generation forecast on a schedule, typically once per day or
   more often for tighter intraday accuracy.
3. Persist the forecast in the database.
4. Let the site planning layer combine forecasted PV generation with load,
   price curves, grid constraints, and battery state to produce the operating
   plan for today and the next day.

## Configuration

Configure the host with:

```json
{
  "Bess": {
    "SolarForecastSource": "open_meteo"
  },
  "OpenMeteoSolarForecast": {
    "engine_backend": "pvlib_sidecar",
    "python_executable": "python",
    "sidecar_script_path": "tools/solar-forecast-engine/pvlib_openmeteo_engine.py",
    "asset_id": "single-bess-1",
    "latitude": 49.84,
    "longitude": 24.03,
    "tilt_degrees": 25,
    "azimuth_degrees": 0,
    "installed_dc_kw": 900,
    "inverter_ac_kw": 800,
    "output_resolution_minutes": 15,
    "forecast_hours": 48
  }
}
```

A ready-to-copy example is available in
[`config/examples/solar-forecast.open-meteo.json`](../../config/examples/solar-forecast.open-meteo.json).

## API

`GET /site/{assetId}/solar-forecast`

Returns the latest forecast curve with:

- source and model identifiers
- generated timestamp
- horizon boundaries
- time step
- configured DC/AC sizes
- forecast points with power, irradiance, temperature, wind speed, and cloud cover

## Backends

- `pvlib_sidecar`: starts the standalone Python engine and reads its JSON output.
- `embedded`: keeps the existing in-process .NET forecast calculation as a fallback.

## Design Notes

- Weather provider: Open-Meteo Weather Forecast API
- Power model: `pvlib` PVWatts in sidecar mode, GTI + derating/clipping in embedded mode
- Accuracy target: medium / pre-optimization grade, not settlement-grade
