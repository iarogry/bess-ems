-- Preserve existing audit records: incompatible historical evidence causes
-- migration failure and requires operator review, never silent rewriting.
-- Expired claims may record a late observed outcome; expiry forbids starting
-- another write, not recording the outcome of an already initiated request.
ALTER TABLE activation_outbox
    ADD CONSTRAINT ck_activation_outbox_completion_evidence
    CHECK ((
        claim_id IS NULL
        OR (claim_outcome_code IS NULL AND claim_completed_at_utc IS NULL)
        OR (
            claim_outcome_code ~ '^[a-z0-9]([a-z0-9-]{0,94}[a-z0-9])?$'
            AND claim_completed_at_utc >= claimed_at_utc
        )
    ) IS TRUE);
