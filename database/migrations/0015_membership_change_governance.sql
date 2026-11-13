-- Controlled, maker-checker workflow for company membership role changes.
BEGIN;

INSERT INTO authorization_permissions(permission_code, description) VALUES
 ('MEMBERSHIPS_READ', 'مشاهده عضویت‌ها و درخواست‌های تغییر نقش'),
 ('MEMBERSHIPS_REQUEST', 'درخواست ایجاد عضویت یا تغییر نقش کاربر'),
 ('MEMBERSHIPS_REVIEW', 'بررسی و تصویب یا رد درخواست عضویت و تغییر نقش');

INSERT INTO company_roles(role_code, display_name, description) VALUES
 ('FINANCE_MEMBERSHIP_MANAGER', 'مدیر درخواست عضویت', 'مشاهده عضویت‌ها و ثبت درخواست عضویت یا تغییر نقش'),
 ('FINANCE_MEMBERSHIP_REVIEWER', 'بازبین عضویت', 'بررسی مستقل درخواست عضویت و تغییر نقش');

INSERT INTO role_permissions(role_code, permission_code) VALUES
 ('FINANCE_MEMBERSHIP_MANAGER','MEMBERSHIPS_READ'),
 ('FINANCE_MEMBERSHIP_MANAGER','MEMBERSHIPS_REQUEST'),
 ('FINANCE_MEMBERSHIP_REVIEWER','MEMBERSHIPS_READ'),
 ('FINANCE_MEMBERSHIP_REVIEWER','MEMBERSHIPS_REVIEW');

INSERT INTO role_permissions(role_code, permission_code)
SELECT 'FINANCE_ADMIN', permission_code
  FROM authorization_permissions
 WHERE permission_code IN ('MEMBERSHIPS_READ','MEMBERSHIPS_REQUEST','MEMBERSHIPS_REVIEW')
ON CONFLICT DO NOTHING;

CREATE TABLE membership_change_requests (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    target_user_id uuid NOT NULL,
    requested_role_code varchar(60) NOT NULL REFERENCES company_roles(role_code)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    requested_by uuid NOT NULL,
    request_status varchar(12) NOT NULL DEFAULT 'PENDING',
    reviewed_by uuid NULL,
    reviewed_at timestamptz NULL,
    reason varchar(1000) NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_membership_request_company FOREIGN KEY (tenant_id, company_id)
        REFERENCES companies(tenant_id, id),
    CONSTRAINT ck_membership_request_status
        CHECK (request_status IN ('PENDING','APPROVED','REJECTED')),
    CONSTRAINT ck_membership_request_review_pair
        CHECK (
          (request_status = 'PENDING' AND reviewed_by IS NULL AND reviewed_at IS NULL)
          OR
          (request_status IN ('APPROVED','REJECTED') AND reviewed_by IS NOT NULL
             AND reviewed_at IS NOT NULL AND reviewed_by <> requested_by)
        )
);

CREATE UNIQUE INDEX uq_membership_one_pending_change
    ON membership_change_requests(tenant_id, company_id, target_user_id)
    WHERE request_status = 'PENDING';

CREATE INDEX ix_membership_requests_company_status_time
    ON membership_change_requests(tenant_id, company_id, request_status, created_at DESC);

ALTER TABLE membership_change_requests ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_membership_change_requests
    ON membership_change_requests
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

COMMIT;
