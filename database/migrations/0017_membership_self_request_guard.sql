-- Prevent a membership manager from requesting a role change for their own identity.
BEGIN;

ALTER TABLE membership_change_requests
    ADD CONSTRAINT ck_membership_request_no_self_request
    CHECK (requested_by <> target_user_id);

COMMIT;
