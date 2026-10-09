CREATE TABLE IF NOT EXISTS shadow_plan_snapshots (
    snapshot_id UUID PRIMARY KEY,
    site_id TEXT NOT NULL,
    delivery_date DATE NOT NULL,
    side TEXT NOT NULL,
    recorded_at TIMESTAMPTZ NOT NULL,
    payload_ready BOOLEAN NOT NULL,
    snapshot_hash TEXT NOT NULL,
    snapshot_json JSONB NOT NULL,
    CHECK (side IN ('Legacy', 'Shadow')),
    UNIQUE (site_id, delivery_date, side, snapshot_hash)
);

CREATE INDEX IF NOT EXISTS idx_shadow_plan_snapshots_lookup
    ON shadow_plan_snapshots (
        site_id,
        delivery_date,
        side,
        recorded_at DESC,
        snapshot_id DESC
    );

CREATE TABLE IF NOT EXISTS shadow_plan_comparisons (
    comparison_id UUID PRIMARY KEY,
    run_id UUID NOT NULL UNIQUE REFERENCES orchestration_runs (run_id) ON DELETE CASCADE,
    site_id TEXT NOT NULL,
    delivery_date DATE NOT NULL,
    compared_at_utc TIMESTAMPTZ NOT NULL,
    is_equivalent BOOLEAN NOT NULL,
    legacy_payload_ready BOOLEAN NOT NULL,
    shadow_payload_ready BOOLEAN NOT NULL,
    mismatches_json JSONB NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_shadow_plan_comparisons_latest
    ON shadow_plan_comparisons (
        site_id,
        delivery_date,
        compared_at_utc DESC,
        comparison_id DESC
    );
