-- Year-end closing safeguards: temporary accounts must be cleared before period closure.
BEGIN;

CREATE UNIQUE INDEX uq_one_closing_journal_per_period
    ON journal_entries(tenant_id, company_id, fiscal_period_id)
    WHERE journal_type = 'CLOSING';

CREATE OR REPLACE FUNCTION guard_fiscal_period_transition()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    IF NEW.period_status = OLD.period_status THEN
        RETURN NEW;
    END IF;

    IF NOT (
        (OLD.period_status = 'OPEN' AND NEW.period_status = 'CLOSED') OR
        (OLD.period_status = 'CLOSED' AND NEW.period_status = 'LOCKED')
    ) THEN
        RAISE EXCEPTION 'Invalid fiscal period transition: % -> %',
            OLD.period_status, NEW.period_status;
    END IF;

    IF OLD.period_status = 'OPEN' AND NEW.period_status = 'CLOSED' THEN
        IF EXISTS (
            SELECT 1 FROM journal_entries j
             WHERE j.tenant_id = OLD.tenant_id AND j.company_id = OLD.company_id
               AND j.fiscal_period_id = OLD.id
               AND j.entry_status NOT IN ('POSTED','REVERSED')
        ) THEN
            RAISE EXCEPTION 'Cannot close a fiscal period while draft, pending, or approved journals remain';
        END IF;

        -- Revenue and expense accounts are temporary. They must have zero net
        -- movement after the approved closing journal has been posted.
        IF EXISTS (
            SELECT 1
              FROM accounts a
              JOIN general_ledger_lines gl
                ON gl.tenant_id=a.tenant_id AND gl.company_id=a.company_id
               AND gl.account_id=a.id AND gl.fiscal_period_id=OLD.id
             WHERE a.tenant_id=OLD.tenant_id AND a.company_id=OLD.company_id
               AND a.account_class IN ('REVENUE','EXPENSE')
             GROUP BY a.id
            HAVING sum(gl.debit_amount - gl.credit_amount) <> 0
        ) THEN
            RAISE EXCEPTION 'Cannot close a fiscal period while revenue or expense accounts retain balances';
        END IF;
    END IF;

    RETURN NEW;
END;
$function$;

COMMIT;
