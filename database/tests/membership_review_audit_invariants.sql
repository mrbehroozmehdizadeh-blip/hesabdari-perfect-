-- Rollback-only checks for requester/target separation and reviewer rationale.
BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conrelid='membership_change_requests'::regclass
           AND conname='ck_membership_request_no_self_request'
    ) THEN
        RAISE EXCEPTION 'A requester must not request a role change for their own identity';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
         WHERE table_schema=current_schema()
           AND table_name='membership_change_requests'
           AND column_name='review_reason'
           AND character_maximum_length=1000
    ) THEN
        RAISE EXCEPTION 'Reviewer rationale column is missing';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conrelid='membership_change_requests'::regclass
           AND conname='ck_membership_review_reason'
    ) THEN
        RAISE EXCEPTION 'Rejected requests must retain a non-empty reviewer rationale';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conrelid='membership_change_requests'::regclass
           AND conname='ck_membership_request_no_self_target_review'
    ) THEN
        RAISE EXCEPTION 'A reviewer must not review a request targeting their own identity';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conrelid='membership_change_requests'::regclass
           AND conname='ck_membership_request_no_finance_admin'
    ) THEN
        RAISE EXCEPTION 'FINANCE_ADMIN must remain outside ordinary membership requests';
    END IF;
END $$;

ROLLBACK;
