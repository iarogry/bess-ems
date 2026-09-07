CREATE TABLE IF NOT EXISTS solar_forecasts (
    asset_id text NOT NULL PRIMARY KEY,
    source text NOT NULL,
    model text NOT NULL,
    generated_at timestamptz NOT NULL,
    horizon_start timestamptz NOT NULL,
    horizon_end timestamptz NOT NULL,
    time_step_seconds integer NOT NULL,
    installed_dc_kw double precision NOT NULL,
    installed_ac_kw double precision NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT ck_solar_forecasts_horizon CHECK (horizon_start < horizon_end),
    CONSTRAINT ck_solar_forecasts_time_step_positive CHECK (time_step_seconds > 0),
    CONSTRAINT ck_solar_forecasts_installed_dc_positive CHECK (installed_dc_kw > 0),
    CONSTRAINT ck_solar_forecasts_installed_ac_positive CHECK (installed_ac_kw > 0)
);

CREATE TABLE IF NOT EXISTS solar_forecast_points (
    asset_id text NOT NULL REFERENCES solar_forecasts(asset_id) ON DELETE CASCADE,
    generated_at timestamptz NOT NULL,
    timestamp timestamptz NOT NULL,
    power_kw double precision NOT NULL,
    irradiance_w_per_square_meter double precision NOT NULL,
    ambient_temperature_celsius double precision NOT NULL,
    wind_speed_meters_per_second double precision NOT NULL,
    cloud_cover_percent integer NOT NULL,
    CONSTRAINT pk_solar_forecast_points PRIMARY KEY (asset_id, timestamp),
    CONSTRAINT ck_solar_forecast_points_power_non_negative CHECK (power_kw >= 0),
    CONSTRAINT ck_solar_forecast_points_irradiance_non_negative CHECK (irradiance_w_per_square_meter >= 0),
    CONSTRAINT ck_solar_forecast_points_wind_non_negative CHECK (wind_speed_meters_per_second >= 0),
    CONSTRAINT ck_solar_forecast_points_cloud_cover CHECK (cloud_cover_percent >= 0 AND cloud_cover_percent <= 100)
);

CREATE INDEX IF NOT EXISTS ix_solar_forecast_points_asset_time
    ON solar_forecast_points (asset_id, timestamp);
