-- Rollback-only checks for company membership role referential integrity.
BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
          FROM pg_constraint c
          JOIN pg_class t ON t.oid = c.conrelid
         WHERE t.relname = 'company_memberships'
           AND c.conname = 'fk_company_memberships_role'
           AND c.contype = 'f'
           AND c.convalidated
    ) THEN
        RAISE EXCEPTION 'Company memberships must reference a validated role catalog entry';
    END IF;

    IF EXISTS (
        SELECT 1
          FROM company_memberships m
          LEFT JOIN company_roles r ON r.role_code = m.role_code
         WHERE r.role_code IS NULL
    ) THEN
        RAISE EXCEPTION 'Every company membership must resolve to a catalog role';
    END IF;

    IF NOT EXISTS (
        SELECT 1
          FROM pg_indexes
         WHERE schemaname = current_schema()
           AND indexname = 'ix_company_memberships_company_role_active'
    ) THEN
        RAISE EXCEPTION 'Company membership role lookup index is missing';
    END IF;
END $$;

ROLLBACK;
