-- Rollback-only tests for mandatory and mutually exclusive journal analytics.
BEGIN;

DO $$
DECLARE
    v_tenant uuid := gen_random_uuid();
    v_company uuid := gen_random_uuid();
    v_period uuid := gen_random_uuid();
    v_root uuid := gen_random_uuid();
    v_fixed_account uuid := gen_random_uuid();
    v_float_account uuid := gen_random_uuid();
    v_fixed_detail uuid := gen_random_uuid();
    v_dimension uuid := gen_random_uuid();
    v_journal uuid := gen_random_uuid();
    v_fixed_line uuid := gen_random_uuid();
    v_float_line uuid := gen_random_uuid();
    v_actor uuid := gen_random_uuid();
BEGIN
    INSERT INTO tenants(id,tenant_code,display_name)
    VALUES(v_tenant,'ANALYTICS-'||replace(v_tenant::text,'-',''),'Analytics test');
    INSERT INTO companies(id,tenant_id,company_code,legal_name,functional_currency)
    VALUES(v_company,v_tenant,'AT','Analytics test company','IRR');
    INSERT INTO fiscal_periods(id,tenant_id,company_id,period_code,starts_on,ends_on)
    VALUES(v_period,v_tenant,v_company,'1405',DATE '2026-03-21',DATE '2027-03-20');

    INSERT INTO accounts(id,tenant_id,company_id,account_code,account_name,account_level,account_class,normal_balance,is_postable)
    VALUES(v_root,v_tenant,v_company,'1','Root',1,'ASSET','DEBIT',false);
    INSERT INTO accounts(id,tenant_id,company_id,parent_account_id,account_code,account_name,account_level,account_class,normal_balance,detail_mode,is_postable)
    VALUES
      (v_fixed_account,v_tenant,v_company,v_root,'11','Fixed',2,'ASSET','DEBIT','FIXED',true),
      (v_float_account,v_tenant,v_company,v_root,'12','Floating',2,'ASSET','DEBIT','FLOATING',true);

    INSERT INTO fixed_account_details(id,tenant_id,company_id,account_id,detail_code,detail_name)
    VALUES(v_fixed_detail,v_tenant,v_company,v_fixed_account,'F-1','Fixed detail');
    INSERT INTO floating_dimensions(id,tenant_id,company_id,dimension_type,dimension_code,dimension_name)
    VALUES(v_dimension,v_tenant,v_company,'COST_CENTER','CC-1','Cost center');

    INSERT INTO journal_entries(id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,currency_code,
                                 entry_status,description,idempotency_key,request_fingerprint,created_by)
    VALUES(v_journal,v_tenant,v_company,v_period,'AN-1',DATE '2026-04-01','IRR',
           'DRAFT','Analytics validation','AN-'||v_journal::text,repeat('a',64),v_actor);

    INSERT INTO journal_lines(id,tenant_id,company_id,journal_entry_id,line_number,account_id,debit_amount,credit_amount)
    VALUES
      (v_fixed_line,v_tenant,v_company,v_journal,1,v_fixed_account,100,0),
      (v_float_line,v_tenant,v_company,v_journal,2,v_float_account,0,100);

    BEGIN
        UPDATE journal_entries SET entry_status='PENDING'
         WHERE tenant_id=v_tenant AND company_id=v_company AND id=v_journal;
        RAISE EXCEPTION 'expected missing analytics rejection';
    EXCEPTION WHEN check_violation THEN
        NULL;
    END;

    INSERT INTO journal_line_fixed_details(tenant_id,company_id,journal_line_id,fixed_detail_id)
    VALUES(v_tenant,v_company,v_fixed_line,v_fixed_detail);
    INSERT INTO journal_line_dimensions(tenant_id,company_id,journal_line_id,dimension_id)
    VALUES(v_tenant,v_company,v_float_line,v_dimension);

    UPDATE journal_entries SET entry_status='PENDING'
     WHERE tenant_id=v_tenant AND company_id=v_company AND id=v_journal;

    BEGIN
        INSERT INTO journal_line_dimensions(tenant_id,company_id,journal_line_id,dimension_id)
        VALUES(v_tenant,v_company,v_fixed_line,v_dimension);
        RAISE EXCEPTION 'expected floating dimension mode rejection';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM='expected floating dimension mode rejection' THEN RAISE; END IF;
    END;
END $$;

ROLLBACK;
