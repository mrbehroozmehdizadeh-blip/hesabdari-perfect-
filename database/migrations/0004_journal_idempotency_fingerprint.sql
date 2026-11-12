-- Persist a canonical request fingerprint so an idempotency key cannot silently
-- return a different draft when a client accidentally reuses the key.
BEGIN;

ALTER TABLE journal_entries
    ADD COLUMN request_fingerprint char(64) NULL;

ALTER TABLE journal_entries
    ADD CONSTRAINT ck_journal_request_fingerprint
    CHECK (request_fingerprint IS NULL OR request_fingerprint ~ '^[0-9a-f]{64}$');

COMMIT;
