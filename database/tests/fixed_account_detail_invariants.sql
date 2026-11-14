-- Rollback-only invariants for fixed analytical details and journal-line assignment.
BEGIN;

DO $$
DECLARE
    v_tenant uuid := gen_random_uuid();
    v_company uuid := gen_random_uuid();
    v_root uuid := gen_random_uuid();
    v_fixed_account uuid := gen_random_uuid();
    v_float_account uuid := gen_random_uuid();
    v_fixed_detail uuid := gen_random_uuid();
    v_second_detail uuid := gen_random_uuid();
BEGIN
    INSERT INTO tenants(id, tenant_code, display_name)
    VALUES (v_tenant, 'FIXED-TEST-' || replace(v_tenant::text, '-', ''), 'Fixed detail invariant test');

    INSERT INTO companies(id, tenant_id, company_code, legal_name, functional_currency)
    VALUES (v_company, v_tenant, 'TEST', 'Fixed detail invariant company', 'IRR');

    INSERT INTO accounts(id, tenant_id, company_id, account_code, account_name,
                         account_level, account_class, normal_balance, is_postable)
    VALUES (v_root, v_tenant, v_company, '100', 'دارایی‌ها', 1, 'ASSET', 'DEBIT', false);

    INSERT INTO accounts(id, tenant_id, company_id, parent_account_id, account_code, account_name,
                         account_level, account_class, normal_balance, detail_mode, is_postable)
    VALUES
      (v_fixed_account, v_tenant, v_company, v_root, '101', 'حساب تفصیلی ثابت', 2, 'ASSET', 'DEBIT', 'FIXED', true),
      (v_float_account, v_tenant, v_company, v_root, '102', 'حساب تفصیلی شناور', 2, 'ASSET', 'DEBIT', 'FLOATING', true);

    INSERT INTO fixed_account_details(id, tenant_id, company_id, account_id, detail_code, detail_name)
    VALUES (v_fixed_detail, v_tenant, v_company, v_fixed_account, '001', 'شعبه مرکزی');

    BEGIN
        INSERT INTO fixed_account_details(tenant_id, company_id, account_id, detail_code, detail_name)
        VALUES (v_tenant, v_company, v_float_account, '001', 'نباید برای حساب شناور پذیرفته شود');
        RAISE EXCEPTION 'expected fixed-detail account-mode validation failure';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM = 'expected fixed-detail account-mode validation failure' THEN RAISE; END IF;
    END;

    BEGIN
        INSERT INTO fixed_account_details(tenant_id, company_id, account_id, detail_code, detail_name)
        VALUES (v_tenant, v_company, v_fixed_account, '001', 'کد تکراری');
        RAISE EXCEPTION 'expected fixed-detail duplicate-code failure';
    EXCEPTION WHEN unique_violation THEN
        NULL;
    END;
END $$;

ROLLBACK;
