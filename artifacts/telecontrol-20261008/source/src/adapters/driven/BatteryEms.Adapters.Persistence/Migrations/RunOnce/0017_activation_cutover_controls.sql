ALTER TABLE activation_outbox
    ADD COLUMN release_writer_owner_id TEXT NULL,
    ADD COLUMN release_safety_revision BIGINT NULL,
    ADD COLUMN release_fencing_token BIGINT NULL,
    ADD COLUMN released_by TEXT NULL,
    ADD COLUMN release_reason TEXT NULL,
    ADD COLUMN released_at_utc TIMESTAMPTZ NULL;

ALTER TABLE activation_outbox
    ADD CONSTRAINT ck_activation_outbox_release_metadata
    CHECK (
        status IN ('Held', 'Cancelled')
        OR (
            length(trim(release_writer_owner_id)) > 0
            AND release_safety_revision > 0
            AND release_fencing_token > 0
            AND length(trim(released_by)) > 0
            AND length(trim(release_reason)) > 0
            AND released_at_utc IS NOT NULL
        )
    );

ALTER TABLE activation_writer_safety
    ADD COLUMN last_rollback_operation_id UUID NULL,
    ADD COLUMN last_rollback_cancelled_count INTEGER NULL;

ALTER TABLE activation_writer_safety
    ADD CONSTRAINT ck_activation_writer_rollback_replay
    CHECK (
        (last_rollback_operation_id IS NULL AND last_rollback_cancelled_count IS NULL)
        OR (
            last_rollback_operation_id IS NOT NULL
            AND last_rollback_cancelled_count >= 0
        )
    );
