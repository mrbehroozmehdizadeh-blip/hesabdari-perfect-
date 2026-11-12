-- Rollback-only smoke tests for fiscal-period state transitions.
BEGIN;

DO $$
DECLARE
    tenant_key uuid := gen_random_uuid();
    company_key uuid := gen_random_uuid();
    period_key uuid := gen_random_uuid();
    rejected boolean := false;
    opening_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO tenants(id, tenant_code, display_name)
    VALUES (tenant_key, 'CI-' || replace(tenant_key::text,'-',''), 'CI fiscal test');

    INSERT INTO companies(id, tenant_id, company_code, legal_name, functional_currency)
    VALUES (company_key, tenant_key, 'CI', 'CI fiscal test', 'EUR');

    INSERT INTO fiscal_periods(id, tenant_id, company_id, period_code, starts_on, ends_on)
    VALUES (period_key, tenant_key, company_key, 'FY-CI', DATE '2026-01-01', DATE '2026-12-31');

    INSERT INTO journal_entries
        (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,currency_code,
         idempotency_key,created_by,journal_type)
    VALUES
        (opening_id,tenant_key,company_key,period_key,'OPEN-CI-1',DATE '2026-01-01',
         'EUR','OPEN-CI-KEY-1',gen_random_uuid(),'OPENING');

    BEGIN
        INSERT INTO journal_entries
            (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,currency_code,
             idempotency_key,created_by,journal_type)
        VALUES
            (gen_random_uuid(),tenant_key,company_key,period_key,'OPEN-CI-2',DATE '2026-01-01',
             'EUR','OPEN-CI-KEY-2',gen_random_uuid(),'OPENING');
    EXCEPTION WHEN unique_violation THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'A second opening journal for the same period must be rejected';
    END IF;

    rejected := false;
    BEGIN
        UPDATE fiscal_periods SET period_status='CLOSED'
         WHERE tenant_id=tenant_key AND company_id=company_key AND id=period_key;
    EXCEPTION WHEN raise_exception THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'A period with an unposted opening journal must not close';
    END IF;

    DELETE FROM journal_entries WHERE id=opening_id;

    UPDATE fiscal_periods SET period_status='CLOSED'
     WHERE tenant_id=tenant_key AND company_id=company_key AND id=period_key;

    IF (SELECT period_status FROM fiscal_periods WHERE id=period_key) <> 'CLOSED' THEN
        RAISE EXCEPTION 'Expected period to close';
    END IF;

    BEGIN
        UPDATE fiscal_periods SET period_status='OPEN'
         WHERE tenant_id=tenant_key AND company_id=company_key AND id=period_key;
    EXCEPTION WHEN raise_exception THEN
        rejected := true;
    END;

    IF NOT rejected THEN
        RAISE EXCEPTION 'Reopening a closed period must be rejected';
    END IF;

    UPDATE fiscal_periods SET period_status='LOCKED'
     WHERE tenant_id=tenant_key AND company_id=company_key AND id=period_key;

    IF (SELECT period_status FROM fiscal_periods WHERE id=period_key) <> 'LOCKED' THEN
        RAISE EXCEPTION 'Expected period to lock';
    END IF;
END $$;

ROLLBACK;
