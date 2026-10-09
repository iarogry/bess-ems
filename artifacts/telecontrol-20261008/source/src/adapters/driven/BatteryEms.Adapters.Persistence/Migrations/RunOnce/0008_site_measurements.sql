CREATE TABLE IF NOT EXISTS site_measurements (
    site_id text NOT NULL,
    source text NOT NULL,
    instrument_type text NOT NULL,
    instrument_id text NOT NULL,
    instrument_name text NOT NULL,
    group_parent_id text NULL,
    scale double precision NULL,
    timestamp timestamptz NOT NULL,
    interval_seconds integer NOT NULL,
    quality text NOT NULL,
    metric text NOT NULL,
    value double precision NOT NULL,
    unit text NOT NULL,
    metadata_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    imported_at timestamptz NOT NULL,
    CONSTRAINT pk_site_measurements
        PRIMARY KEY (
            site_id,
            source,
            instrument_type,
            instrument_id,
            timestamp,
            interval_seconds,
            metric
        ),
    CONSTRAINT ck_site_measurements_interval_positive
        CHECK (interval_seconds >= 0),
    CONSTRAINT ck_site_measurements_metadata_object
        CHECK (jsonb_typeof(metadata_json) = 'object')
);

CREATE INDEX IF NOT EXISTS ix_site_measurements_site_time
    ON site_measurements (site_id, timestamp);

CREATE INDEX IF NOT EXISTS ix_site_measurements_instrument_time
    ON site_measurements (source, instrument_type, instrument_id, timestamp);

CREATE INDEX IF NOT EXISTS ix_site_measurements_metric_time
    ON site_measurements (site_id, metric, timestamp);
