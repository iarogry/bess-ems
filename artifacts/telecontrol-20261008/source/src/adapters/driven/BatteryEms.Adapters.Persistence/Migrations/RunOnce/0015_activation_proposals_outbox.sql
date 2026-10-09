CREATE TABLE IF NOT EXISTS activation_proposals (
    proposal_id UUID PRIMARY KEY,
    comparison_id UUID NOT NULL REFERENCES shadow_plan_comparisons (comparison_id) ON DELETE RESTRICT,
    run_id UUID NOT NULL REFERENCES orchestration_runs (run_id) ON DELETE RESTRICT,
    site_id TEXT NOT NULL,
    delivery_date DATE NOT NULL,
    payload_hash TEXT NOT NULL,
    payload_json JSONB NOT NULL,
    proposed_by TEXT NOT NULL,
    proposal_reason TEXT NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    status TEXT NOT NULL,
    reviewed_by TEXT NULL,
    review_reason TEXT NULL,
    reviewed_at_utc TIMESTAMPTZ NULL,
    outbox_item_id UUID NULL UNIQUE,
    CHECK (status IN ('Pending', 'Approved', 'Rejected', 'Expired', 'Cancelled')),
    CHECK (expires_at_utc > created_at_utc),
    UNIQUE (comparison_id, payload_hash)
);

CREATE INDEX IF NOT EXISTS idx_activation_proposals_site_date
    ON activation_proposals (site_id, delivery_date, created_at_utc DESC);

CREATE TABLE IF NOT EXISTS activation_outbox (
    outbox_item_id UUID PRIMARY KEY,
    proposal_id UUID NOT NULL UNIQUE REFERENCES activation_proposals (proposal_id) ON DELETE RESTRICT,
    site_id TEXT NOT NULL,
    delivery_date DATE NOT NULL,
    payload_hash TEXT NOT NULL,
    payload_json JSONB NOT NULL,
    idempotency_key TEXT NOT NULL UNIQUE,
    status TEXT NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    updated_at_utc TIMESTAMPTZ NULL,
    CHECK (status IN ('Held', 'Ready', 'Claimed', 'Succeeded', 'Failed', 'Cancelled'))
);

CREATE INDEX IF NOT EXISTS idx_activation_outbox_status
    ON activation_outbox (status, created_at_utc, outbox_item_id);
