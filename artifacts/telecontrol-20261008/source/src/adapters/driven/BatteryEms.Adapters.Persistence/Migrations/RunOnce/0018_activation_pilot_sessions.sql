CREATE TABLE IF NOT EXISTS activation_pilot_sessions (
    session_id UUID PRIMARY KEY,
    proposal_id UUID NOT NULL REFERENCES activation_proposals (proposal_id) ON DELETE RESTRICT,
    outbox_item_id UUID NOT NULL REFERENCES activation_outbox (outbox_item_id) ON DELETE RESTRICT,
    site_id TEXT NOT NULL,
    writer_owner_id TEXT NOT NULL,
    safety_revision BIGINT NOT NULL,
    fencing_token BIGINT NOT NULL,
    status TEXT NOT NULL,
    armed_by TEXT NOT NULL,
    arm_reason TEXT NOT NULL,
    armed_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    aborted_by TEXT NULL,
    abort_reason TEXT NULL,
    aborted_at_utc TIMESTAMPTZ NULL,
    CHECK (status IN ('Armed', 'Aborted', 'Expired')),
    CHECK (safety_revision > 0),
    CHECK (fencing_token > 0),
    CHECK (expires_at_utc > armed_at_utc),
    CHECK (expires_at_utc <= armed_at_utc + INTERVAL '15 minutes'),
    CHECK (
        (status = 'Aborted'
            AND length(trim(aborted_by)) > 0
            AND length(trim(abort_reason)) > 0
            AND aborted_at_utc IS NOT NULL)
        OR
        (status <> 'Aborted'
            AND aborted_by IS NULL
            AND abort_reason IS NULL
            AND aborted_at_utc IS NULL)
    )
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_activation_pilot_sessions_active_site
    ON activation_pilot_sessions (site_id)
    WHERE status = 'Armed';

CREATE UNIQUE INDEX IF NOT EXISTS ux_activation_pilot_sessions_active_outbox
    ON activation_pilot_sessions (outbox_item_id)
    WHERE status = 'Armed';

ALTER TABLE activation_outbox
    ADD COLUMN release_pilot_session_id UUID NULL
        REFERENCES activation_pilot_sessions (session_id) ON DELETE RESTRICT;

-- Untagged dollar quoting avoids DbUp variable substitution of the guard.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM activation_outbox
        WHERE status IN ('Ready', 'Claimed')
          AND release_pilot_session_id IS NULL
    ) THEN
        RAISE EXCEPTION
            'activation pilot migration blocked: rollback all Ready/Claimed outbox items before upgrade';
    END IF;
END
$$;

ALTER TABLE activation_outbox
    DROP CONSTRAINT ck_activation_outbox_release_metadata;

ALTER TABLE activation_outbox
    ADD CONSTRAINT ck_activation_outbox_release_metadata
    CHECK (
        status IN ('Held', 'Cancelled', 'Succeeded', 'Failed')
        OR (
            length(trim(release_writer_owner_id)) > 0
            AND release_safety_revision > 0
            AND release_fencing_token > 0
            AND release_pilot_session_id IS NOT NULL
            AND length(trim(released_by)) > 0
            AND length(trim(release_reason)) > 0
            AND released_at_utc IS NOT NULL
        )
    );
