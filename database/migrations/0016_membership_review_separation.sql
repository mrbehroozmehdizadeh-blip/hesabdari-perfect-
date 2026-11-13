-- Strengthen membership governance: a reviewer cannot review their own target membership,
-- and the super-administrator role is excluded from the ordinary request workflow.
BEGIN;

ALTER TABLE membership_change_requests
    ADD CONSTRAINT ck_membership_request_no_self_target_review
    CHECK (
      request_status = 'PENDING'
      OR reviewed_by IS DISTINCT FROM target_user_id
    );

ALTER TABLE membership_change_requests
    ADD CONSTRAINT ck_membership_request_no_finance_admin
    CHECK (requested_role_code <> 'FINANCE_ADMIN');

COMMIT;
