-- Company-level authorization memberships. Existing JWT tenant claims alone do not grant access to every company.
BEGIN;
CREATE TABLE company_memberships (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    user_id uuid NOT NULL,
    role_code varchar(60) NOT NULL DEFAULT 'FINANCE_VIEWER',
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_membership_company FOREIGN KEY (tenant_id, company_id)
        REFERENCES companies(tenant_id, id),
    CONSTRAINT uq_company_membership UNIQUE (tenant_id, company_id, user_id),
    CONSTRAINT ck_membership_role CHECK (role_code ~ '^[A-Z][A-Z0-9_]{1,59}$')
);
CREATE INDEX ix_company_memberships_user
    ON company_memberships(tenant_id, user_id, is_active);
ALTER TABLE company_memberships ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_company_memberships ON company_memberships
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
COMMIT;
