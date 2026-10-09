ALTER TABLE activation_outbox
    ADD COLUMN claim_id UUID NULL UNIQUE,
    ADD COLUMN claim_executor_id TEXT NULL,
    ADD COLUMN claim_safety_revision BIGINT NULL,
    ADD COLUMN claim_fencing_token BIGINT NULL,
    ADD COLUMN claim_pilot_session_id UUID NULL
        REFERENCES activation_pilot_sessions (session_id) ON DELETE RESTRICT,
    ADD COLUMN claimed_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN claim_expires_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN claim_outcome_code TEXT NULL,
    ADD COLUMN claim_completed_at_utc TIMESTAMPTZ NULL;

ALTER TABLE activation_outbox
    ADD CONSTRAINT ck_activation_outbox_claim_metadata
    CHECK (
        (
            status IN ('Held', 'Ready', 'Cancelled')
            AND claim_id IS NULL
            AND claim_executor_id IS NULL
            AND claim_safety_revision IS NULL
            AND claim_fencing_token IS NULL
            AND claim_pilot_session_id IS NULL
            AND claimed_at_utc IS NULL
            AND claim_expires_at_utc IS NULL
            AND claim_outcome_code IS NULL
            AND claim_completed_at_utc IS NULL
        )
        OR (
            status = 'Claimed'
            AND claim_id IS NOT NULL
            AND length(trim(claim_executor_id)) > 0
            AND claim_safety_revision > 0
            AND claim_fencing_token > 0
            AND claim_pilot_session_id IS NOT NULL
            AND claimed_at_utc IS NOT NULL
            AND claim_expires_at_utc > claimed_at_utc
            AND claim_expires_at_utc <= claimed_at_utc + INTERVAL '30 seconds'
            AND claim_outcome_code IS NULL
            AND claim_completed_at_utc IS NULL
        )
        OR (
            status IN ('Succeeded', 'Failed')
            AND (
                (
                    claim_id IS NULL
                    AND claim_executor_id IS NULL
                    AND claim_safety_revision IS NULL
                    AND claim_fencing_token IS NULL
                    AND claim_pilot_session_id IS NULL
                    AND claimed_at_utc IS NULL
                    AND claim_expires_at_utc IS NULL
                    AND claim_outcome_code IS NULL
                    AND claim_completed_at_utc IS NULL
                )
                OR (
                    claim_id IS NOT NULL
                    AND length(trim(claim_executor_id)) > 0
                    AND claim_safety_revision > 0
                    AND claim_fencing_token > 0
                    AND claim_pilot_session_id IS NOT NULL
                    AND claimed_at_utc IS NOT NULL
                    AND claim_expires_at_utc > claimed_at_utc
                    AND claim_expires_at_utc <= claimed_at_utc + INTERVAL '30 seconds'
                    AND length(trim(claim_outcome_code)) > 0
                    AND claim_completed_at_utc IS NOT NULL
                )
            )
        )
    );

CREATE INDEX IF NOT EXISTS idx_activation_outbox_claim_expiry
    ON activation_outbox (claim_expires_at_utc, outbox_item_id)
    WHERE status = 'Claimed';
