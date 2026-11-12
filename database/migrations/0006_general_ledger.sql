-- Append-only general ledger postings. Journal drafts/approvals remain operational documents;
-- the ledger is populated only by the atomic posting workflow.
BEGIN;

ALTER TABLE journal_entries
    ADD COLUMN reversal_of_journal_id uuid NULL;

ALTER TABLE journal_entries
    ADD CONSTRAINT fk_journal_reversal_source
    FOREIGN KEY (tenant_id, company_id, reversal_of_journal_id)
    REFERENCES journal_entries(tenant_id, company_id, id);

CREATE UNIQUE INDEX uq_journal_single_reversal
    ON journal_entries(tenant_id, company_id, reversal_of_journal_id)
    WHERE reversal_of_journal_id IS NOT NULL;

CREATE TABLE general_ledger_lines (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    fiscal_period_id uuid NOT NULL,
    journal_entry_id uuid NOT NULL,
    journal_line_id uuid NOT NULL,
    account_id uuid NOT NULL,
    entry_date date NOT NULL,
    currency_code char(3) NOT NULL,
    debit_amount numeric(28,8) NOT NULL DEFAULT 0,
    credit_amount numeric(28,8) NOT NULL DEFAULT 0,
    posted_by uuid NOT NULL,
    posted_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_gl_company FOREIGN KEY (tenant_id, company_id)
        REFERENCES companies(tenant_id, id),
    CONSTRAINT fk_gl_period FOREIGN KEY (tenant_id, company_id, fiscal_period_id)
        REFERENCES fiscal_periods(tenant_id, company_id, id),
    CONSTRAINT fk_gl_journal FOREIGN KEY (tenant_id, company_id, journal_entry_id)
        REFERENCES journal_entries(tenant_id, company_id, id),
    CONSTRAINT fk_gl_journal_line FOREIGN KEY (tenant_id, company_id, journal_line_id)
        REFERENCES journal_lines(tenant_id, company_id, id),
    CONSTRAINT fk_gl_account FOREIGN KEY (tenant_id, company_id, account_id)
        REFERENCES accounts(tenant_id, company_id, id),
    CONSTRAINT uq_gl_source_line UNIQUE (tenant_id, company_id, journal_line_id),
    CONSTRAINT ck_gl_currency CHECK (currency_code ~ '^[A-Z]{3}$'),
    CONSTRAINT ck_gl_amounts_nonnegative CHECK (debit_amount >= 0 AND credit_amount >= 0),
    CONSTRAINT ck_gl_one_side CHECK (
        (debit_amount > 0 AND credit_amount = 0) OR
        (credit_amount > 0 AND debit_amount = 0)
    )
);

CREATE INDEX ix_gl_account_date
    ON general_ledger_lines(tenant_id, company_id, account_id, entry_date, fiscal_period_id);
CREATE INDEX ix_gl_period_date
    ON general_ledger_lines(tenant_id, company_id, fiscal_period_id, entry_date);
CREATE INDEX ix_gl_journal
    ON general_ledger_lines(tenant_id, company_id, journal_entry_id);

ALTER TABLE general_ledger_lines ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_general_ledger ON general_ledger_lines
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

CREATE OR REPLACE FUNCTION reject_general_ledger_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'General ledger postings are append-only; use a reversal journal for corrections';
END;
$function$;

CREATE TRIGGER trg_general_ledger_append_only
BEFORE UPDATE OR DELETE ON general_ledger_lines
FOR EACH ROW EXECUTE FUNCTION reject_general_ledger_mutation();

COMMIT;
