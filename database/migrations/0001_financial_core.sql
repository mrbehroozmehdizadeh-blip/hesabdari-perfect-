-- Initial financial core schema.
-- Apply only to a NEW development database after review; this does not migrate Floot data.
BEGIN;

CREATE TABLE tenants (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_code varchar(50) NOT NULL UNIQUE,
    display_name varchar(200) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE companies (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    company_code varchar(50) NOT NULL,
    legal_name varchar(250) NOT NULL,
    functional_currency char(3) NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_companies_tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_companies_code UNIQUE (tenant_id, company_code),
    CONSTRAINT ck_company_currency CHECK (functional_currency ~ '^[A-Z]{3}$')
);

CREATE TABLE fiscal_periods (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    period_code varchar(30) NOT NULL,
    starts_on date NOT NULL,
    ends_on date NOT NULL,
    period_status varchar(12) NOT NULL DEFAULT 'OPEN',
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_period_company FOREIGN KEY (tenant_id, company_id)
        REFERENCES companies(tenant_id, id),
    CONSTRAINT uq_period_tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_period_company_code UNIQUE (tenant_id, company_id, period_code),
    CONSTRAINT ck_period_dates CHECK (ends_on >= starts_on),
    CONSTRAINT ck_period_status CHECK (period_status IN ('OPEN', 'CLOSED', 'LOCKED'))
);

CREATE TABLE accounts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    parent_account_id uuid NULL,
    account_code varchar(50) NOT NULL,
    account_name varchar(250) NOT NULL,
    account_level smallint NOT NULL,
    account_class varchar(12) NOT NULL,
    normal_balance varchar(6) NOT NULL,
    detail_mode varchar(10) NOT NULL DEFAULT 'NONE',
    is_postable boolean NOT NULL DEFAULT false,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_account_company FOREIGN KEY (tenant_id, company_id)
        REFERENCES companies(tenant_id, id),
    CONSTRAINT fk_account_parent FOREIGN KEY (tenant_id, company_id, parent_account_id)
        REFERENCES accounts(tenant_id, company_id, id),
    CONSTRAINT uq_account_tenant_company_id UNIQUE (tenant_id, company_id, id),
    CONSTRAINT uq_account_code UNIQUE (tenant_id, company_id, account_code),
    CONSTRAINT ck_account_level CHECK (account_level BETWEEN 1 AND 10),
    CONSTRAINT ck_account_class CHECK (account_class IN ('ASSET','LIABILITY','EQUITY','REVENUE','EXPENSE')),
    CONSTRAINT ck_account_balance CHECK (normal_balance IN ('DEBIT','CREDIT')),
    CONSTRAINT ck_account_detail_mode CHECK (detail_mode IN ('NONE','FIXED','FLOATING')),
    CONSTRAINT ck_account_parent_level CHECK (
        (account_level = 1 AND parent_account_id IS NULL) OR
        (account_level > 1 AND parent_account_id IS NOT NULL)
    )
);

CREATE TABLE floating_dimensions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    dimension_type varchar(40) NOT NULL,
    dimension_code varchar(60) NOT NULL,
    dimension_name varchar(250) NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_dimension_company FOREIGN KEY (tenant_id, company_id)
        REFERENCES companies(tenant_id, id),
    CONSTRAINT uq_dimension_tenant_company_id UNIQUE (tenant_id, company_id, id),
    CONSTRAINT uq_dimension_code UNIQUE (tenant_id, company_id, dimension_type, dimension_code)
);

CREATE TABLE journal_entries (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    fiscal_period_id uuid NOT NULL,
    entry_number varchar(50) NOT NULL,
    entry_date date NOT NULL,
    currency_code char(3) NOT NULL,
    entry_status varchar(12) NOT NULL DEFAULT 'DRAFT',
    description varchar(1000) NOT NULL DEFAULT '',
    idempotency_key varchar(120) NOT NULL,
    created_by uuid NOT NULL,
    approved_by uuid NULL,
    posted_by uuid NULL,
    posted_at timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_journal_company FOREIGN KEY (tenant_id, company_id)
        REFERENCES companies(tenant_id, id),
    CONSTRAINT fk_journal_period FOREIGN KEY (tenant_id, fiscal_period_id)
        REFERENCES fiscal_periods(tenant_id, id),
    CONSTRAINT uq_journal_tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_journal_number UNIQUE (tenant_id, company_id, entry_number),
    CONSTRAINT uq_journal_idempotency UNIQUE (tenant_id, company_id, idempotency_key),
    CONSTRAINT ck_journal_currency CHECK (currency_code ~ '^[A-Z]{3}$'),
    CONSTRAINT ck_journal_status CHECK (entry_status IN ('DRAFT','PENDING','APPROVED','POSTED','REVERSED'))
);

CREATE TABLE journal_lines (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    journal_entry_id uuid NOT NULL,
    company_id uuid NOT NULL,
    line_number integer NOT NULL,
    account_id uuid NOT NULL,
    debit_amount numeric(28,8) NOT NULL DEFAULT 0,
    credit_amount numeric(28,8) NOT NULL DEFAULT 0,
    description varchar(500) NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_line_journal FOREIGN KEY (tenant_id, journal_entry_id)
        REFERENCES journal_entries(tenant_id, id),
    CONSTRAINT fk_line_account FOREIGN KEY (tenant_id, company_id, account_id)
        REFERENCES accounts(tenant_id, company_id, id),
    CONSTRAINT uq_journal_line_number UNIQUE (tenant_id, journal_entry_id, line_number),
    CONSTRAINT ck_line_number CHECK (line_number > 0),
    CONSTRAINT ck_line_amounts_nonnegative CHECK (debit_amount >= 0 AND credit_amount >= 0),
    CONSTRAINT ck_line_one_side CHECK (
        (debit_amount > 0 AND credit_amount = 0) OR
        (credit_amount > 0 AND debit_amount = 0)
    )
);

CREATE TABLE journal_line_dimensions (
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    journal_line_id uuid NOT NULL,
    dimension_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, journal_line_id, dimension_id),
    CONSTRAINT fk_line_dimension_line FOREIGN KEY (tenant_id, journal_line_id)
        REFERENCES journal_lines(tenant_id, id),
    CONSTRAINT fk_line_dimension_dimension FOREIGN KEY (tenant_id, company_id, dimension_id)
        REFERENCES floating_dimensions(tenant_id, company_id, id)
);

CREATE TABLE audit_events (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NULL,
    actor_id uuid NOT NULL,
    event_type varchar(100) NOT NULL,
    entity_type varchar(100) NOT NULL,
    entity_id uuid NULL,
    correlation_id uuid NULL,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    payload jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX ix_accounts_company_parent ON accounts(tenant_id, company_id, parent_account_id);
CREATE INDEX ix_journal_entries_company_date ON journal_entries(tenant_id, company_id, entry_date);
CREATE INDEX ix_journal_entries_status ON journal_entries(tenant_id, company_id, entry_status);
CREATE INDEX ix_journal_lines_account ON journal_lines(tenant_id, company_id, account_id);
CREATE INDEX ix_audit_events_tenant_time ON audit_events(tenant_id, occurred_at DESC);

-- Tenant isolation is enforced in PostgreSQL as defense in depth.
ALTER TABLE companies ENABLE ROW LEVEL SECURITY;
ALTER TABLE fiscal_periods ENABLE ROW LEVEL SECURITY;
ALTER TABLE accounts ENABLE ROW LEVEL SECURITY;
ALTER TABLE floating_dimensions ENABLE ROW LEVEL SECURITY;
ALTER TABLE journal_entries ENABLE ROW LEVEL SECURITY;
ALTER TABLE journal_lines ENABLE ROW LEVEL SECURITY;
ALTER TABLE journal_line_dimensions ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_companies ON companies
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_isolation_periods ON fiscal_periods
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_isolation_accounts ON accounts
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_isolation_dimensions ON floating_dimensions
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_isolation_journals ON journal_entries
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_isolation_journal_lines ON journal_lines
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_isolation_line_dimensions ON journal_line_dimensions
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_isolation_audit ON audit_events
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

COMMIT;
