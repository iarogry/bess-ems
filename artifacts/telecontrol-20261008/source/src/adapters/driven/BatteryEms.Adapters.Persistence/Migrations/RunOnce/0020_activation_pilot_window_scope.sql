-- Untagged dollar quoting avoids DbUp variable substitution of the guard.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM activation_pilot_sessions) THEN
        RAISE EXCEPTION
            'activation window-scope migration blocked: existing pilot history requires an operator-reviewed upgrade preserving audit records';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM activation_outbox
        WHERE status IN ('Ready', 'Claimed')
           OR release_pilot_session_id IS NOT NULL
           OR claim_id IS NOT NULL
    ) THEN
        RAISE EXCEPTION
            'activation window-scope migration blocked: rollback all released or claimed outbox items before upgrade';
    END IF;
END
$$;

ALTER TABLE activation_pilot_sessions
    ADD COLUMN window_id TEXT NOT NULL,
    ADD CONSTRAINT ck_activation_pilot_window_id
        CHECK (window_id IN ('Z1', 'Z2', 'Z3', 'Z4'));

ALTER TABLE activation_outbox
    ADD COLUMN release_window_id TEXT NULL,
    ADD COLUMN claim_window_id TEXT NULL,
    ADD COLUMN claim_window_payload_hash TEXT NULL,
    ADD CONSTRAINT ck_activation_outbox_release_window_id
        CHECK (release_window_id IS NULL OR release_window_id IN ('Z1', 'Z2', 'Z3', 'Z4')),
    ADD CONSTRAINT ck_activation_outbox_claim_window_id
        CHECK (claim_window_id IS NULL OR claim_window_id IN ('Z1', 'Z2', 'Z3', 'Z4')),
    ADD CONSTRAINT ck_activation_outbox_claim_window_binding
        CHECK ((
            (claim_id IS NULL AND claim_window_id IS NULL AND claim_window_payload_hash IS NULL)
            OR (
                claim_id IS NOT NULL
                AND claim_window_id = release_window_id
                AND claim_pilot_session_id = release_pilot_session_id
                AND claim_window_payload_hash ~ '^[0-9A-F]{64}$'
            )
        ) IS TRUE);

ALTER TABLE activation_outbox
    DROP CONSTRAINT ck_activation_outbox_release_metadata,
    DROP CONSTRAINT ck_activation_outbox_claim_metadata;

ALTER TABLE activation_outbox
    ADD CONSTRAINT ck_activation_outbox_release_metadata
    CHECK ((
        status IN ('Held', 'Cancelled')
        OR (
            status IN ('Ready', 'Claimed')
            AND length(trim(release_writer_owner_id)) > 0
            AND release_safety_revision > 0
            AND release_fencing_token > 0
            AND release_pilot_session_id IS NOT NULL
            AND release_window_id IN ('Z1', 'Z2', 'Z3', 'Z4')
            AND length(trim(released_by)) > 0
            AND length(trim(release_reason)) > 0
            AND released_at_utc IS NOT NULL
        )
        OR (
            status IN ('Succeeded', 'Failed')
            AND (
                release_pilot_session_id IS NULL
                OR (
                    length(trim(release_writer_owner_id)) > 0
                    AND release_safety_revision > 0
                    AND release_fencing_token > 0
                    AND release_window_id IN ('Z1', 'Z2', 'Z3', 'Z4')
                    AND length(trim(released_by)) > 0
                    AND length(trim(release_reason)) > 0
                    AND released_at_utc IS NOT NULL
                )
            )
        )
    ) IS TRUE);

ALTER TABLE activation_outbox
    ADD CONSTRAINT ck_activation_outbox_claim_metadata
    CHECK ((
        (
            status IN ('Held', 'Ready', 'Cancelled')
            AND claim_id IS NULL
            AND claim_executor_id IS NULL
            AND claim_safety_revision IS NULL
            AND claim_fencing_token IS NULL
            AND claim_pilot_session_id IS NULL
            AND claim_window_id IS NULL
            AND claim_window_payload_hash IS NULL
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
            AND claim_window_id IN ('Z1', 'Z2', 'Z3', 'Z4')
            AND length(trim(claim_window_payload_hash)) > 0
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
                    AND claim_window_id IS NULL
                    AND claim_window_payload_hash IS NULL
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
                    AND claim_window_id IN ('Z1', 'Z2', 'Z3', 'Z4')
                    AND length(trim(claim_window_payload_hash)) > 0
                    AND claimed_at_utc IS NOT NULL
                    AND claim_expires_at_utc > claimed_at_utc
                    AND claim_expires_at_utc <= claimed_at_utc + INTERVAL '30 seconds'
                    AND length(trim(claim_outcome_code)) > 0
                    AND claim_completed_at_utc IS NOT NULL
                )
            )
        )
    ) IS TRUE);
