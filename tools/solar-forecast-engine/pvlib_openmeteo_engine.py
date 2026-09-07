#!/usr/bin/env python
"""Standalone PV forecast engine using pvlib + Open-Meteo.

This script is intentionally independent from the .NET host so it can evolve
into a sidecar/runtime-isolated forecasting engine.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any


def _bootstrap_vendor_packages() -> None:
    repo_root = Path(__file__).resolve().parents[2]
    vendor_dir = repo_root / ".tmp-pvlib"
    if vendor_dir.exists():
        sys.path.insert(0, str(vendor_dir))


_bootstrap_vendor_packages()

import pandas as pd  # noqa: E402
import pvlib  # noqa: E402
import requests  # noqa: E402


SOURCE_ID = "open-meteo"
MODEL_ID = "pvlib-pvwatts-open-meteo"


@dataclass(frozen=True)
class EngineConfig:
    asset_id: str
    latitude: float
    longitude: float
    tilt_degrees: float
    azimuth_degrees: float
    installed_dc_kw: float
    inverter_ac_kw: float
    temperature_coefficient_per_degree: float
    system_loss_fraction: float
    forecast_hours: int
    output_resolution_minutes: int
    base_url: str
    weather_model: str
    http_timeout_seconds: int

    @staticmethod
    def from_dict(payload: dict[str, Any]) -> "EngineConfig":
        return EngineConfig(
            asset_id=str(payload["asset_id"]),
            latitude=float(payload["latitude"]),
            longitude=float(payload["longitude"]),
            tilt_degrees=float(payload.get("tilt_degrees", 25)),
            azimuth_degrees=float(payload.get("azimuth_degrees", 0)),
            installed_dc_kw=float(payload["installed_dc_kw"]),
            inverter_ac_kw=float(payload["inverter_ac_kw"]),
            temperature_coefficient_per_degree=float(
                payload.get("temperature_coefficient_per_degree", -0.004)
            ),
            system_loss_fraction=float(payload.get("system_loss_fraction", 0.14)),
            forecast_hours=int(payload.get("forecast_hours", 48)),
            output_resolution_minutes=int(payload.get("output_resolution_minutes", 15)),
            base_url=str(payload.get("base_url", "https://api.open-meteo.com/v1/forecast")),
            weather_model=str(payload.get("weather_model", "best_match")),
            http_timeout_seconds=int(payload.get("http_timeout_seconds", 30)),
        )


def load_config(path: Path) -> EngineConfig:
    payload = json.loads(path.read_text(encoding="utf-8"))
    source = payload.get("OpenMeteoSolarForecast", payload)
    return EngineConfig.from_dict(source)


def load_config_from_stdin() -> EngineConfig:
    payload = json.loads(sys.stdin.read())
    source = payload.get("OpenMeteoSolarForecast", payload)
    return EngineConfig.from_dict(source)


def fetch_weather(config: EngineConfig) -> pd.DataFrame:
    response = requests.get(
        config.base_url,
        params={
            "latitude": config.latitude,
            "longitude": config.longitude,
            "timezone": "UTC",
            "models": config.weather_model,
            "forecast_hours": config.forecast_hours,
            "tilt": config.tilt_degrees,
            "azimuth": config.azimuth_degrees,
            "hourly": ",".join(
                [
                    "global_tilted_irradiance",
                    "temperature_2m",
                    "wind_speed_10m",
                    "cloud_cover",
                ]
            ),
        },
        timeout=config.http_timeout_seconds,
    )
    response.raise_for_status()
    payload = response.json()["hourly"]
    frame = pd.DataFrame(
        {
            "timestamp": pd.to_datetime(payload["time"], utc=True),
            "gti": payload["global_tilted_irradiance"],
            "temp_air": payload["temperature_2m"],
            "wind_speed": payload["wind_speed_10m"],
            "cloud_cover": payload["cloud_cover"],
        }
    )
    return frame.set_index("timestamp")


def run_model(weather: pd.DataFrame, config: EngineConfig) -> pd.DataFrame:
    cell_temperature = pvlib.temperature.faiman(
        poa_global=weather["gti"],
        temp_air=weather["temp_air"],
        wind_speed=weather["wind_speed"],
    )
    pdc_w = pvlib.pvsystem.pvwatts_dc(
        effective_irradiance=weather["gti"],
        temp_cell=cell_temperature,
        pdc0=config.installed_dc_kw * 1000,
        gamma_pdc=config.temperature_coefficient_per_degree,
    )
    pdc_w = pdc_w * (1.0 - config.system_loss_fraction)
    pac_w = pvlib.inverter.pvwatts(
        pdc=pdc_w,
        pdc0=config.inverter_ac_kw * 1000,
    )

    modeled = weather.copy()
    modeled["power_kw"] = (
        pd.Series(pac_w, index=weather.index)
        .fillna(0.0)
        .clip(lower=0.0, upper=config.inverter_ac_kw * 1000)
        / 1000.0
    )
    return modeled


def resample_output(modeled: pd.DataFrame, config: EngineConfig) -> pd.DataFrame:
    resolution = f"{config.output_resolution_minutes}min"
    if config.output_resolution_minutes == 60:
        return modeled

    return modeled.resample(resolution).interpolate(method="time")


def to_wire_payload(frame: pd.DataFrame, config: EngineConfig) -> dict[str, Any]:
    frame = frame.copy()
    frame["power_kw"] = frame["power_kw"].round(3)
    frame["gti"] = frame["gti"].round(3)
    frame["temp_air"] = frame["temp_air"].round(3)
    frame["wind_speed"] = frame["wind_speed"].round(3)
    points = []
    for timestamp, row in frame.iterrows():
        cloud_cover = int(max(0, min(100, round(float(row["cloud_cover"])))))
        power_kw = float(row["power_kw"])
        if not math.isfinite(power_kw):
            power_kw = 0.0
        points.append(
            {
                "timestamp": timestamp.isoformat().replace("+00:00", "Z"),
                "power_kw": power_kw,
                "irradiance_w_per_square_meter": float(row["gti"]),
                "ambient_temperature_celsius": float(row["temp_air"]),
                "wind_speed_meters_per_second": float(row["wind_speed"]),
                "cloud_cover_percent": cloud_cover,
            }
        )

    return {
        "asset_id": config.asset_id,
        "source": SOURCE_ID,
        "model": MODEL_ID,
        "generated_at": pd.Timestamp.now("UTC").isoformat().replace("+00:00", "Z"),
        "horizon_start": points[0]["timestamp"],
        "horizon_end": (
            frame.index[-1] + pd.Timedelta(minutes=config.output_resolution_minutes)
        )
        .isoformat()
        .replace("+00:00", "Z"),
        "time_step_seconds": config.output_resolution_minutes * 60,
        "installed_dc_kw": config.installed_dc_kw,
        "installed_ac_kw": config.inverter_ac_kw,
        "points": points,
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="PV forecast engine using pvlib + Open-Meteo.")
    parser.add_argument("--config", type=Path, help="Path to a JSON config file containing OpenMeteoSolarForecast settings.")
    parser.add_argument("--stdin-json", action="store_true", help="Read JSON config from stdin instead of a file.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.stdin_json:
        config = load_config_from_stdin()
    elif args.config is not None:
        config = load_config(args.config)
    else:
        raise SystemExit("Either --config or --stdin-json must be provided.")
    weather = fetch_weather(config)
    modeled = run_model(weather, config)
    output = resample_output(modeled, config)
    print(json.dumps(to_wire_payload(output, config), ensure_ascii=True, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
