-- Bind every company membership to a role in the centrally managed catalog.
-- Existing rows are validated before the migration is considered complete.
BEGIN;

ALTER TABLE company_memberships
    ADD CONSTRAINT fk_company_memberships_role
    FOREIGN KEY (role_code)
    REFERENCES company_roles(role_code)
    ON UPDATE RESTRICT
    ON DELETE RESTRICT
    NOT VALID;

ALTER TABLE company_memberships
    VALIDATE CONSTRAINT fk_company_memberships_role;

CREATE INDEX ix_company_memberships_company_role_active
    ON company_memberships(tenant_id, company_id, role_code, is_active);

COMMIT;
