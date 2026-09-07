CREATE TABLE IF NOT EXISTS site_consumption_readings (
    site_id text NOT NULL,
    source text NOT NULL,
    point_id text NOT NULL,
    point_name text NOT NULL,
    timestamp timestamptz NOT NULL,
    apoz double precision NULL,
    aneg double precision NULL,
    ppoz double precision NULL,
    pneg double precision NULL,
    interval_seconds integer NULL,
    metadata_json text NULL,
    imported_at timestamptz NOT NULL,
    CONSTRAINT pk_site_consumption_readings
        PRIMARY KEY (site_id, source, point_id, timestamp)
);

CREATE INDEX IF NOT EXISTS ix_site_consumption_readings_site_time
    ON site_consumption_readings (site_id, timestamp);

CREATE INDEX IF NOT EXISTS ix_site_consumption_readings_point_time
    ON site_consumption_readings (point_id, timestamp);
