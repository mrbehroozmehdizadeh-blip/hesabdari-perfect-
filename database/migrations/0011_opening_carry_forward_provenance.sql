-- Provenance for controlled carry-forward opening journals.
BEGIN;

ALTER TABLE journal_entries
    ADD COLUMN opening_source_period_id uuid NULL;

ALTER TABLE journal_entries
    ADD CONSTRAINT fk_journal_opening_source_period
    FOREIGN KEY (tenant_id, company_id, opening_source_period_id)
    REFERENCES fiscal_periods(tenant_id, company_id, id);

ALTER TABLE journal_entries
    ADD CONSTRAINT ck_opening_source_period_type
    CHECK (opening_source_period_id IS NULL OR journal_type = 'OPENING');

ALTER TABLE journal_entries
    ADD CONSTRAINT ck_opening_source_not_target
    CHECK (opening_source_period_id IS NULL OR opening_source_period_id <> fiscal_period_id);

CREATE UNIQUE INDEX uq_one_carry_forward_per_source_period
    ON journal_entries(tenant_id, company_id, opening_source_period_id)
    WHERE opening_source_period_id IS NOT NULL;

COMMIT;
