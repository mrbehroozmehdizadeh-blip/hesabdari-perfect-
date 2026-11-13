-- Rollback-only invariants for account hierarchy and posting detail policy.
BEGIN;

DO $$
DECLARE
    v_tenant uuid := gen_random_uuid();
    v_company uuid := gen_random_uuid();
    v_root uuid := gen_random_uuid();
    v_posting uuid := gen_random_uuid();
    v_other_class uuid := gen_random_uuid();
BEGIN
    INSERT INTO tenants(id, tenant_code, display_name)
    VALUES (v_tenant, 'ACCT-TEST-' || replace(v_tenant::text, '-', ''), 'Account invariant test');

    INSERT INTO companies(id, tenant_id, company_code, legal_name, functional_currency)
    VALUES (v_company, v_tenant, 'TEST', 'Account invariant test company', 'IRR');

    INSERT INTO accounts(id, tenant_id, company_id, account_code, account_name,
                         account_level, account_class, normal_balance, is_postable)
    VALUES (v_root, v_tenant, v_company, '100', 'دارایی‌ها', 1, 'ASSET', 'DEBIT', false);

    INSERT INTO accounts(id, tenant_id, company_id, parent_account_id, account_code, account_name,
                         account_level, account_class, normal_balance, detail_mode, is_postable)
    VALUES (v_posting, v_tenant, v_company, v_root, '101', 'صندوق', 2, 'ASSET', 'DEBIT', 'FIXED', true);

    BEGIN
        INSERT INTO accounts(tenant_id, company_id, parent_account_id, account_code, account_name,
                             account_level, account_class, normal_balance, is_postable)
        VALUES (v_tenant, v_company, v_root, '102', 'سطح والد نامعتبر', 3, 'ASSET', 'DEBIT', true);
        RAISE EXCEPTION 'expected parent-level validation failure';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM = 'expected parent-level validation failure' THEN RAISE; END IF;
    END;

    BEGIN
        INSERT INTO accounts(tenant_id, company_id, parent_account_id, account_code, account_name,
                             account_level, account_class, normal_balance, is_postable)
        VALUES (v_tenant, v_company, v_posting, '103', 'فرزند حساب قابل ثبت', 3, 'ASSET', 'DEBIT', true);
        RAISE EXCEPTION 'expected posting-parent validation failure';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM = 'expected posting-parent validation failure' THEN RAISE; END IF;
    END;

    BEGIN
        INSERT INTO accounts(tenant_id, company_id, parent_account_id, account_code, account_name,
                             account_level, account_class, normal_balance, is_postable)
        VALUES (v_tenant, v_company, v_root, '104', 'طبقه ناسازگار', 2, 'LIABILITY', 'CREDIT', true);
        RAISE EXCEPTION 'expected account-class validation failure';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM = 'expected account-class validation failure' THEN RAISE; END IF;
    END;

    BEGIN
        INSERT INTO accounts(tenant_id, company_id, account_code, account_name,
                             account_level, account_class, normal_balance, detail_mode, is_postable)
        VALUES (v_tenant, v_company, '105', 'گروه با تفصیلی', 1, 'ASSET', 'DEBIT', 'FIXED', false);
        RAISE EXCEPTION 'expected group detail-mode validation failure';
    EXCEPTION WHEN check_violation THEN
        NULL;
    END;

    BEGIN
        UPDATE accounts SET account_class='LIABILITY'
         WHERE tenant_id=v_tenant AND company_id=v_company AND id=v_root;
        RAISE EXCEPTION 'expected parent class update validation failure';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM = 'expected parent class update validation failure' THEN RAISE; END IF;
    END;
END $$;

ROLLBACK;
