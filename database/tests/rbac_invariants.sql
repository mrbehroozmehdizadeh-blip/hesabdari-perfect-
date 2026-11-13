-- Rollback-only checks for the centralized company-role permission catalog.
BEGIN;

DO $$
BEGIN
    IF (SELECT count(*) FROM authorization_permissions) <> 21 THEN
        RAISE EXCEPTION 'Expected 21 authorization permissions after membership governance';
    END IF;

    IF (SELECT count(*) FROM company_roles) <> 11 THEN
        RAISE EXCEPTION 'Expected 11 seeded finance roles after membership governance';
    END IF;

    IF (SELECT count(*) FROM role_permissions WHERE role_code='FINANCE_ADMIN')
       <> (SELECT count(*) FROM authorization_permissions) THEN
        RAISE EXCEPTION 'FINANCE_ADMIN must receive every catalog permission';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM role_permissions
         WHERE role_code='FINANCE_ACCOUNTANT' AND permission_code='JOURNALS_CREATE'
    ) OR EXISTS (
        SELECT 1 FROM role_permissions
         WHERE role_code='FINANCE_ACCOUNTANT' AND permission_code IN ('JOURNALS_APPROVE','JOURNALS_POST')
    ) THEN
        RAISE EXCEPTION 'Accountant must create journals but must not approve or post them';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM role_permissions
         WHERE role_code='FINANCE_APPROVER' AND permission_code='JOURNALS_APPROVE'
    ) OR EXISTS (
        SELECT 1 FROM role_permissions
         WHERE role_code='FINANCE_APPROVER' AND permission_code='JOURNALS_POST'
    ) THEN
        RAISE EXCEPTION 'Approver must not receive posting permission';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM role_permissions
         WHERE role_code='FINANCE_POSTER' AND permission_code='JOURNALS_POST'
    ) OR EXISTS (
        SELECT 1 FROM role_permissions
         WHERE role_code='FINANCE_POSTER' AND permission_code='JOURNALS_APPROVE'
    ) THEN
        RAISE EXCEPTION 'Poster must not receive approval permission';
    END IF;

    IF EXISTS (
        SELECT 1 FROM role_permissions
         WHERE role_code='FINANCE_VIEWER'
           AND permission_code IN ('ACCOUNTS_MANAGE','JOURNALS_CREATE','JOURNALS_APPROVE','JOURNALS_POST')
    ) THEN
        RAISE EXCEPTION 'Viewer role must remain read-only';
    END IF;
END $$;

ROLLBACK;
