CREATE TABLE deye_day_authorizations (
    authorization_id UUID PRIMARY KEY,
    proposal_id UUID NOT NULL UNIQUE REFERENCES activation_proposals(proposal_id) ON DELETE RESTRICT,
    site_id TEXT NOT NULL,
    delivery_date DATE NOT NULL,
    payload_hash TEXT NOT NULL CHECK (payload_hash ~ '^[0-9A-F]{64}$'),
    writer_owner_id TEXT NOT NULL CHECK (length(trim(writer_owner_id)) > 0),
    safety_revision BIGINT NOT NULL CHECK (safety_revision > 0),
    authorized_by TEXT NOT NULL CHECK (length(trim(authorized_by)) > 0),
    reason TEXT NOT NULL CHECK (length(trim(reason)) > 0),
    created_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL CHECK (expires_at_utc > created_at_utc),
    revoked BOOLEAN NOT NULL DEFAULT FALSE,
    revoked_by TEXT NULL,
    revoke_reason TEXT NULL,
    revoked_at_utc TIMESTAMPTZ NULL,
    UNIQUE(site_id, delivery_date),
    UNIQUE(authorization_id, site_id, delivery_date),
    CHECK (((NOT revoked AND revoked_by IS NULL AND revoke_reason IS NULL AND revoked_at_utc IS NULL)
        OR (revoked AND length(trim(revoked_by)) > 0 AND length(trim(revoke_reason)) > 0 AND revoked_at_utc >= created_at_utc)) IS TRUE)
);

CREATE TABLE deye_day_window_claims (
    claim_id UUID PRIMARY KEY,
    authorization_id UUID NOT NULL,
    site_id TEXT NOT NULL,
    delivery_date DATE NOT NULL,
    window_id TEXT NOT NULL CHECK (window_id IN ('Z1', 'Z2', 'Z3', 'Z4')),
    payload_hash TEXT NOT NULL CHECK (payload_hash ~ '^[0-9A-F]{64}$'),
    window_payload_hash TEXT NOT NULL CHECK (window_payload_hash ~ '^[0-9A-F]{64}$'),
    writer_owner_id TEXT NOT NULL,
    safety_revision BIGINT NOT NULL CHECK (safety_revision > 0),
    fencing_token BIGINT NOT NULL CHECK (fencing_token > 0),
    claimed_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    CHECK (expires_at_utc > claimed_at_utc AND expires_at_utc <= claimed_at_utc + INTERVAL '30 seconds'),
    UNIQUE(site_id, delivery_date, window_id),
    FOREIGN KEY (authorization_id, site_id, delivery_date)
        REFERENCES deye_day_authorizations(authorization_id, site_id, delivery_date) ON DELETE RESTRICT
);
