CREATE TABLE IF NOT EXISTS orchestration_runs (
    run_id UUID PRIMARY KEY,
    site_id TEXT NOT NULL,
    run_type TEXT NOT NULL,
    horizon_start TIMESTAMPTZ NULL,
    horizon_end TIMESTAMPTZ NULL,
    trigger_type TEXT NOT NULL,
    trigger_ref TEXT NULL,
    idempotency_key TEXT NOT NULL UNIQUE,
    status TEXT NOT NULL,
    started_at TIMESTAMPTZ NOT NULL,
    completed_at TIMESTAMPTZ NULL,
    input_hash TEXT NULL,
    output_ref TEXT NULL,
    error_code TEXT NULL,
    error_message TEXT NULL,
    metadata_json JSONB NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX IF NOT EXISTS idx_orchestration_runs_site_started_at
    ON orchestration_runs (site_id, started_at DESC);

CREATE TABLE IF NOT EXISTS orchestration_steps (
    step_id UUID PRIMARY KEY,
    run_id UUID NOT NULL REFERENCES orchestration_runs (run_id) ON DELETE CASCADE,
    site_id TEXT NOT NULL,
    module TEXT NOT NULL,
    step_order INTEGER NOT NULL,
    idempotency_key TEXT NOT NULL UNIQUE,
    status TEXT NOT NULL,
    started_at TIMESTAMPTZ NULL,
    completed_at TIMESTAMPTZ NULL,
    attempt_count INTEGER NOT NULL,
    next_attempt_at TIMESTAMPTZ NULL,
    input_hash TEXT NULL,
    output_ref TEXT NULL,
    data_role TEXT NULL,
    data_status TEXT NULL,
    error_code TEXT NULL,
    error_message TEXT NULL,
    metadata_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    CHECK (step_order >= 0),
    CHECK (attempt_count >= 0)
);

CREATE INDEX IF NOT EXISTS idx_orchestration_steps_run_order
    ON orchestration_steps (run_id, step_order ASC);

CREATE TABLE IF NOT EXISTS orchestration_locks (
    lock_key TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL,
    acquired_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    metadata_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    CHECK (acquired_at < expires_at)
);

CREATE INDEX IF NOT EXISTS idx_orchestration_locks_expires_at
    ON orchestration_locks (expires_at ASC);

CREATE TABLE IF NOT EXISTS orchestration_data_balances (
    site_id TEXT NOT NULL,
    data_group TEXT NOT NULL,
    source TEXT NOT NULL,
    instrument_id TEXT NOT NULL,
    role TEXT NOT NULL,
    status TEXT NOT NULL,
    freshness_deadline_utc TIMESTAMPTZ NULL,
    last_success_at_utc TIMESTAMPTZ NULL,
    last_attempt_at_utc TIMESTAMPTZ NULL,
    next_attempt_at_utc TIMESTAMPTZ NULL,
    attempt_count INTEGER NOT NULL,
    last_error_code TEXT NULL,
    last_error_message TEXT NULL,
    quality_summary TEXT NULL,
    metadata_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (site_id, data_group, source, instrument_id),
    CHECK (attempt_count >= 0)
);

CREATE INDEX IF NOT EXISTS idx_orchestration_data_balances_site_status
    ON orchestration_data_balances (site_id, status);
