-- Preserve the independent reviewer's rationale separately from the requester's reason.
BEGIN;

ALTER TABLE membership_change_requests
    ADD COLUMN review_reason varchar(1000) NULL;

ALTER TABLE membership_change_requests
    ADD CONSTRAINT ck_membership_review_reason
    CHECK (
      (request_status = 'PENDING' AND review_reason IS NULL)
      OR
      (request_status = 'APPROVED' AND (review_reason IS NULL OR length(btrim(review_reason)) > 0))
      OR
      (request_status = 'REJECTED' AND review_reason IS NOT NULL AND length(btrim(review_reason)) > 0)
    );

COMMIT;
