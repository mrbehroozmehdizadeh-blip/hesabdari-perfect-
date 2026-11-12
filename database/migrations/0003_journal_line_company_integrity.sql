-- Strengthen journal-line ownership: a line must belong to the same tenant AND company
-- as its parent journal entry. This is a forward-only additive migration.
BEGIN;

ALTER TABLE journal_entries
    ADD CONSTRAINT uq_journal_tenant_company_id
    UNIQUE (tenant_id, company_id, id);

ALTER TABLE journal_lines
    DROP CONSTRAINT fk_line_journal;

ALTER TABLE journal_lines
    ADD CONSTRAINT fk_line_journal_company
    FOREIGN KEY (tenant_id, company_id, journal_entry_id)
    REFERENCES journal_entries (tenant_id, company_id, id);

COMMIT;
