BEGIN;

-- Global authorization catalog. Tenant membership selects a role; this catalog
-- determines what that role may do. Application identities should receive SELECT
-- only on these tables; changes are migration/admin operations, never user input.
CREATE TABLE authorization_permissions (
    permission_code varchar(80) PRIMARY KEY,
    description varchar(250) NOT NULL,
    CONSTRAINT ck_authorization_permission_code
        CHECK (permission_code ~ '^[A-Z][A-Z0-9_]{1,79}$')
);

CREATE TABLE company_roles (
    role_code varchar(60) PRIMARY KEY,
    display_name varchar(120) NOT NULL,
    description varchar(500) NOT NULL,
    is_system boolean NOT NULL DEFAULT true,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_company_role_code
        CHECK (role_code ~ '^[A-Z][A-Z0-9_]{1,59}$')
);

CREATE TABLE role_permissions (
    role_code varchar(60) NOT NULL REFERENCES company_roles(role_code),
    permission_code varchar(80) NOT NULL REFERENCES authorization_permissions(permission_code),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (role_code, permission_code)
);
CREATE INDEX ix_role_permissions_permission
    ON role_permissions(permission_code, role_code);

INSERT INTO authorization_permissions(permission_code, description) VALUES
 ('ACCOUNTS_READ', 'مشاهده سرفصل حساب‌ها'),
 ('ACCOUNTS_MANAGE', 'ایجاد، ویرایش و غیرفعال‌سازی حساب‌ها'),
 ('DIMENSIONS_READ', 'مشاهده ابعاد شناور'),
 ('DIMENSIONS_MANAGE', 'مدیریت ابعاد شناور'),
 ('JOURNALS_READ', 'مشاهده اسناد حسابداری'),
 ('JOURNALS_CREATE', 'ایجاد پیش‌نویس سند'),
 ('JOURNALS_SUBMIT', 'ارسال سند برای تأیید'),
 ('JOURNALS_APPROVE', 'تأیید مستقل سند'),
 ('JOURNALS_POST', 'ثبت قطعی سند در دفتر کل'),
 ('JOURNALS_REVERSE', 'ایجاد سند معکوس'),
 ('LEDGER_READ', 'مشاهده دفتر کل و تراز آزمایشی'),
 ('FISCAL_PERIODS_READ', 'مشاهده دوره‌های مالی'),
 ('FISCAL_PERIOD_CLOSE', 'بستن دوره مالی'),
 ('FISCAL_PERIOD_LOCK', 'قفل نهایی دوره مالی'),
 ('OPENING_BALANCES_CREATE', 'ایجاد سند افتتاحیه'),
 ('YEAR_END_CLOSE', 'ایجاد سند بستن حساب‌های موقت'),
 ('CARRY_FORWARD_CREATE', 'ایجاد سند انتقال مانده'),
 ('CARRY_FORWARD_RECONCILE', 'مشاهده تطبیق انتقال مانده');

INSERT INTO company_roles(role_code, display_name, description) VALUES
 ('FINANCE_ADMIN', 'مدیر مالی سامانه', 'نقش مدیریتی با تمام مجوزهای ثبت‌شده در کاتالوگ'),
 ('FINANCE_VIEWER', 'مشاهده‌گر مالی', 'دسترسی فقط‌خواندنی به اطلاعات مالی'),
 ('FINANCE_ACCOUNTANT', 'حسابدار', 'تهیه اسناد و مدیریت اطلاعات پایه حسابداری'),
 ('FINANCE_APPROVER', 'تأییدکننده مالی', 'تأیید مستقل اسناد'),
 ('FINANCE_POSTER', 'ثبت‌کننده دفتر کل', 'ثبت قطعی اسناد تأییدشده'),
 ('FINANCE_PERIOD_CLOSER', 'مسئول بستن دوره', 'بستن دوره مالی'),
 ('FINANCE_PERIOD_LOCKER', 'مسئول قفل دوره', 'قفل نهایی دوره مالی'),
 ('FINANCE_OPENING_MANAGER', 'مدیر افتتاحیه و انتقال', 'مدیریت اسناد افتتاحیه و انتقال مانده'),
 ('FINANCE_YEAR_END_MANAGER', 'مدیر بستن سال مالی', 'تهیه سند بستن حساب‌های موقت');

INSERT INTO role_permissions(role_code, permission_code)
SELECT 'FINANCE_ADMIN', permission_code FROM authorization_permissions;

INSERT INTO role_permissions(role_code, permission_code) VALUES
 ('FINANCE_VIEWER','ACCOUNTS_READ'),
 ('FINANCE_VIEWER','DIMENSIONS_READ'),
 ('FINANCE_VIEWER','JOURNALS_READ'),
 ('FINANCE_VIEWER','LEDGER_READ'),
 ('FINANCE_VIEWER','FISCAL_PERIODS_READ'),
 ('FINANCE_VIEWER','CARRY_FORWARD_RECONCILE'),

 ('FINANCE_ACCOUNTANT','ACCOUNTS_READ'),
 ('FINANCE_ACCOUNTANT','ACCOUNTS_MANAGE'),
 ('FINANCE_ACCOUNTANT','DIMENSIONS_READ'),
 ('FINANCE_ACCOUNTANT','DIMENSIONS_MANAGE'),
 ('FINANCE_ACCOUNTANT','JOURNALS_READ'),
 ('FINANCE_ACCOUNTANT','JOURNALS_CREATE'),
 ('FINANCE_ACCOUNTANT','JOURNALS_SUBMIT'),
 ('FINANCE_ACCOUNTANT','JOURNALS_REVERSE'),
 ('FINANCE_ACCOUNTANT','LEDGER_READ'),
 ('FINANCE_ACCOUNTANT','FISCAL_PERIODS_READ'),

 ('FINANCE_APPROVER','JOURNALS_READ'),
 ('FINANCE_APPROVER','JOURNALS_APPROVE'),
 ('FINANCE_POSTER','JOURNALS_READ'),
 ('FINANCE_POSTER','JOURNALS_POST'),
 ('FINANCE_PERIOD_CLOSER','FISCAL_PERIODS_READ'),
 ('FINANCE_PERIOD_CLOSER','FISCAL_PERIOD_CLOSE'),
 ('FINANCE_PERIOD_LOCKER','FISCAL_PERIODS_READ'),
 ('FINANCE_PERIOD_LOCKER','FISCAL_PERIOD_LOCK'),
 ('FINANCE_OPENING_MANAGER','FISCAL_PERIODS_READ'),
 ('FINANCE_OPENING_MANAGER','OPENING_BALANCES_CREATE'),
 ('FINANCE_OPENING_MANAGER','CARRY_FORWARD_CREATE'),
 ('FINANCE_OPENING_MANAGER','CARRY_FORWARD_RECONCILE'),
 ('FINANCE_YEAR_END_MANAGER','FISCAL_PERIODS_READ'),
 ('FINANCE_YEAR_END_MANAGER','YEAR_END_CLOSE');

-- Existing membership rows remain intact; their role codes are now resolved
-- against the catalog at authorization time. No membership is auto-created.
COMMIT;
