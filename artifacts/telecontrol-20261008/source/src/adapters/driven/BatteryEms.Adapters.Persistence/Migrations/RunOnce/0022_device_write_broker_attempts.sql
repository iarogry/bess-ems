CREATE TABLE device_write_broker_sites (
    site_id TEXT PRIMARY KEY CHECK (length(trim(site_id)) > 0)
);

CREATE TABLE device_write_broker_attempts (
    attempt_id UUID PRIMARY KEY,
    site_id TEXT NOT NULL REFERENCES device_write_broker_sites(site_id),
    delivery_date DATE NOT NULL,
    window_id TEXT NOT NULL CHECK (window_id IN ('Z1', 'Z2', 'Z3', 'Z4')),
    payload_hash TEXT NOT NULL CHECK (payload_hash ~ '^[0-9A-F]{64}$'),
    authority TEXT NOT NULL CHECK (authority IN ('LegacyRunner', 'ProductAgent')),
    writer_owner_id TEXT NOT NULL CHECK (length(trim(writer_owner_id)) > 0),
    safety_revision BIGINT NOT NULL CHECK (safety_revision > 0),
    fencing_token BIGINT NOT NULL CHECK (fencing_token > 0),
    activation_claim_id UUID NULL,
    state TEXT NOT NULL CHECK (state IN ('Prepared', 'Initiated', 'Unknown', 'Verified', 'NotSent')),
    begun_at_utc TIMESTAMPTZ NOT NULL,
    initiated_at_utc TIMESTAMPTZ NULL,
    observed_at_utc TIMESTAMPTZ NULL,
    outcome_code TEXT NULL,
    CHECK ((
        (authority = 'LegacyRunner' AND activation_claim_id IS NULL)
        OR (authority = 'ProductAgent' AND activation_claim_id IS NOT NULL)
    ) IS TRUE),
    CHECK ((initiated_at_utc IS NULL OR initiated_at_utc >= begun_at_utc) IS TRUE),
    CHECK ((observed_at_utc IS NULL OR observed_at_utc >= COALESCE(initiated_at_utc, begun_at_utc)) IS TRUE),
    CHECK ((
        (state = 'Prepared' AND initiated_at_utc IS NULL AND observed_at_utc IS NULL AND outcome_code IS NULL)
        OR (state = 'Initiated' AND initiated_at_utc IS NOT NULL AND observed_at_utc IS NULL AND outcome_code IS NULL)
        OR (state = 'Unknown' AND observed_at_utc IS NOT NULL AND outcome_code = 'device-write-broker-outcome-unknown')
        OR (state = 'Verified' AND initiated_at_utc IS NOT NULL AND observed_at_utc IS NOT NULL AND outcome_code = 'device-write-broker-readback-matched')
        OR (state = 'NotSent' AND initiated_at_utc IS NULL AND observed_at_utc IS NOT NULL AND outcome_code = 'device-write-broker-not-sent')
    ) IS TRUE)
);

-- No timeout/lease expiry is attached to this latch. Never clear it automatically.
CREATE UNIQUE INDEX ux_device_write_broker_unresolved_site
    ON device_write_broker_attempts(site_id)
    WHERE state IN ('Prepared', 'Initiated', 'Unknown');

-- A consumed product claim cannot be used again after any terminal outcome.
CREATE UNIQUE INDEX ux_device_write_broker_activation_claim
    ON device_write_broker_attempts(activation_claim_id)
    WHERE activation_claim_id IS NOT NULL;

CREATE UNIQUE INDEX ux_device_write_broker_verified_window
    ON device_write_broker_attempts(site_id, delivery_date, window_id)
    WHERE state = 'Verified';
