-- Rollback-only metadata checks for strengthened maker-checker separation.
BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conrelid='membership_change_requests'::regclass
           AND conname='ck_membership_request_no_self_target_review'
    ) THEN
        RAISE EXCEPTION 'Reviewer must be prohibited from reviewing their own target membership';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conrelid='membership_change_requests'::regclass
           AND conname='ck_membership_request_no_finance_admin'
    ) THEN
        RAISE EXCEPTION 'FINANCE_ADMIN must be excluded from ordinary membership requests';
    END IF;
END $$;

ROLLBACK;
