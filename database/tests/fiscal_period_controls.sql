-- Rollback-only smoke tests for fiscal-period state transitions.
BEGIN;

DO $$
DECLARE
    tenant_key uuid := gen_random_uuid();
    company_key uuid := gen_random_uuid();
    period_key uuid := gen_random_uuid();
    rejected boolean := false;
BEGIN
    INSERT INTO tenants(id, tenant_code, display_name)
    VALUES (tenant_key, 'CI-' || replace(tenant_key::text,'-',''), 'CI fiscal test');

    INSERT INTO companies(id, tenant_id, company_code, legal_name, functional_currency)
    VALUES (company_key, tenant_key, 'CI', 'CI fiscal test', 'EUR');

    INSERT INTO fiscal_periods(id, tenant_id, company_id, period_code, starts_on, ends_on)
    VALUES (period_key, tenant_key, company_key, 'FY-CI', DATE '2026-01-01', DATE '2026-12-31');

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
