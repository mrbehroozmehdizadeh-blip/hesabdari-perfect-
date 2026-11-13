-- Rollback-only checks for membership maker-checker governance.
BEGIN;

DO $$
BEGIN
    IF (SELECT count(*) FROM authorization_permissions) <> 21 THEN
        RAISE EXCEPTION 'Expected 21 permissions after membership governance migration';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM role_permissions
         WHERE role_code='FINANCE_MEMBERSHIP_MANAGER' AND permission_code='MEMBERSHIPS_REQUEST'
    ) OR EXISTS (
        SELECT 1 FROM role_permissions
         WHERE role_code='FINANCE_MEMBERSHIP_MANAGER' AND permission_code='MEMBERSHIPS_REVIEW'
    ) THEN
        RAISE EXCEPTION 'Membership manager must request but must not review';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM role_permissions
         WHERE role_code='FINANCE_MEMBERSHIP_REVIEWER' AND permission_code='MEMBERSHIPS_REVIEW'
    ) OR EXISTS (
        SELECT 1 FROM role_permissions
         WHERE role_code='FINANCE_MEMBERSHIP_REVIEWER' AND permission_code='MEMBERSHIPS_REQUEST'
    ) THEN
        RAISE EXCEPTION 'Membership reviewer must review but must not request';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_indexes
         WHERE schemaname=current_schema()
           AND indexname='uq_membership_one_pending_change'
    ) THEN
        RAISE EXCEPTION 'Only-one-pending-request index is missing';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_class c
         JOIN pg_namespace n ON n.oid=c.relnamespace
         WHERE n.nspname=current_schema()
           AND c.relname='membership_change_requests'
           AND c.relrowsecurity
    ) THEN
        RAISE EXCEPTION 'Membership change requests must have row-level security enabled';
    END IF;
END $$;

ROLLBACK;
