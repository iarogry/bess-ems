CREATE TABLE IF NOT EXISTS activation_writer_safety (
    site_id TEXT PRIMARY KEY,
    kill_switch_engaged BOOLEAN NOT NULL DEFAULT TRUE,
    writer_authority TEXT NOT NULL DEFAULT 'LegacyRunner',
    legacy_writer_stopped_at_utc TIMESTAMPTZ NULL,
    legacy_stop_evidence TEXT NULL,
    revision BIGINT NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    updated_by TEXT NOT NULL,
    reason TEXT NOT NULL,
    CHECK (writer_authority IN ('None', 'LegacyRunner', 'ProductAgent')),
    CHECK (revision > 0),
    CHECK (
        writer_authority <> 'ProductAgent'
        OR (
            legacy_writer_stopped_at_utc IS NOT NULL
            AND length(trim(legacy_stop_evidence)) > 0
        )
    )
);

CREATE TABLE IF NOT EXISTS activation_writer_fence_sequences (
    site_id TEXT PRIMARY KEY,
    last_fencing_token BIGINT NOT NULL DEFAULT 0,
    CHECK (last_fencing_token >= 0)
);

CREATE TABLE IF NOT EXISTS activation_writer_leases (
    site_id TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL,
    fencing_token BIGINT NOT NULL,
    acquired_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    CHECK (fencing_token > 0),
    CHECK (expires_at_utc > acquired_at_utc)
);

CREATE INDEX IF NOT EXISTS idx_activation_writer_leases_expiry
    ON activation_writer_leases (expires_at_utc, site_id);
