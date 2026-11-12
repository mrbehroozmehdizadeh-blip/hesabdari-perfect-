-- Rollback-only smoke tests for fiscal-period state transitions.
BEGIN;

DO $$
DECLARE
    tenant_key uuid := gen_random_uuid();
    company_key uuid := gen_random_uuid();
    period_key uuid := gen_random_uuid();
    rejected boolean := false;
    opening_id uuid := gen_random_uuid();
    closing_id uuid := gen_random_uuid();
    carry_id uuid := gen_random_uuid();
    carry_duplicate_id uuid := gen_random_uuid();
    next_period_id uuid := gen_random_uuid();
    later_period_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO tenants(id, tenant_code, display_name)
    VALUES (tenant_key, 'CI-' || replace(tenant_key::text,'-',''), 'CI fiscal test');

    INSERT INTO companies(id, tenant_id, company_code, legal_name, functional_currency)
    VALUES (company_key, tenant_key, 'CI', 'CI fiscal test', 'EUR');

    INSERT INTO fiscal_periods(id, tenant_id, company_id, period_code, starts_on, ends_on)
    VALUES (period_key, tenant_key, company_key, 'FY-CI', DATE '2026-01-01', DATE '2026-12-31');

    INSERT INTO fiscal_periods(id,tenant_id,company_id,period_code,starts_on,ends_on)
    VALUES (next_period_id,tenant_key,company_key,'FY-NEXT',DATE '2027-01-01',DATE '2027-12-31'),
           (later_period_id,tenant_key,company_key,'FY-LATER',DATE '2028-01-01',DATE '2028-12-31');

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

    INSERT INTO journal_entries
        (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,currency_code,
         idempotency_key,created_by,journal_type)
    VALUES
        (closing_id,tenant_key,company_key,period_key,'CLOSE-CI-1',DATE '2026-12-31',
         'EUR','CLOSE-CI-KEY-1',gen_random_uuid(),'CLOSING');

    rejected := false;
    BEGIN
        INSERT INTO journal_entries
            (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,currency_code,
             idempotency_key,created_by,journal_type)
        VALUES
            (gen_random_uuid(),tenant_key,company_key,period_key,'CLOSE-CI-2',DATE '2026-12-31',
             'EUR','CLOSE-CI-KEY-2',gen_random_uuid(),'CLOSING');
    EXCEPTION WHEN unique_violation THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'A second closing journal for the same period must be rejected';
    END IF;

    INSERT INTO journal_entries
        (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,currency_code,
         idempotency_key,created_by,journal_type,opening_source_period_id)
    VALUES
        (carry_id,tenant_key,company_key,next_period_id,'CARRY-CI-1',DATE '2027-01-01',
         'EUR','CARRY-CI-KEY-1',gen_random_uuid(),'OPENING',period_key);

    rejected := false;
    BEGIN
        INSERT INTO journal_entries
            (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,currency_code,
             idempotency_key,created_by,journal_type,opening_source_period_id)
        VALUES
            (carry_duplicate_id,tenant_key,company_key,later_period_id,'CARRY-CI-2',DATE '2028-01-01',
             'EUR','CARRY-CI-KEY-2',gen_random_uuid(),'OPENING',period_key);
    EXCEPTION WHEN unique_violation THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'A source fiscal period must not be carried forward more than once';
    END IF;

    rejected := false;
    BEGIN
        INSERT INTO journal_entries
            (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,currency_code,
             idempotency_key,created_by,journal_type,opening_source_period_id)
        VALUES
            (gen_random_uuid(),tenant_key,company_key,period_key,'CARRY-CI-SELF',DATE '2026-01-01',
             'EUR','CARRY-CI-KEY-SELF',gen_random_uuid(),'OPENING',period_key);
    EXCEPTION WHEN check_violation THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'Carry-forward source and target periods must be different';
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

    DELETE FROM journal_entries WHERE id IN (opening_id, closing_id, carry_id);

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
