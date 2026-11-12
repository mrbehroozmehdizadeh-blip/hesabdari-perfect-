-- Fiscal-period closure and opening-balance classification safeguards.
BEGIN;

ALTER TABLE journal_entries
    ADD COLUMN journal_type varchar(16) NOT NULL DEFAULT 'NORMAL';

ALTER TABLE journal_entries
    ADD CONSTRAINT ck_journal_type
    CHECK (journal_type IN ('NORMAL','OPENING','CLOSING','ADJUSTMENT','REVERSAL'));

CREATE UNIQUE INDEX uq_one_opening_journal_per_period
    ON journal_entries(tenant_id, company_id, fiscal_period_id)
    WHERE journal_type = 'OPENING';

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

    IF OLD.period_status = 'OPEN' AND NEW.period_status = 'CLOSED' AND EXISTS (
        SELECT 1
          FROM journal_entries j
         WHERE j.tenant_id = OLD.tenant_id
           AND j.company_id = OLD.company_id
           AND j.fiscal_period_id = OLD.id
           AND j.entry_status NOT IN ('POSTED','REVERSED')
    ) THEN
        RAISE EXCEPTION 'Cannot close a fiscal period while draft, pending, or approved journals remain';
    END IF;

    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_guard_fiscal_period_transition
BEFORE UPDATE OF period_status ON fiscal_periods
FOR EACH ROW EXECUTE FUNCTION guard_fiscal_period_transition();

-- Every journal mutation serializes against period closure by locking the period row.
-- This closes the race where a journal could be inserted while a period is closing.
CREATE OR REPLACE FUNCTION guard_journal_period_open()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    period_id uuid;
    period_state varchar(12);
BEGIN
    IF TG_OP = 'DELETE' THEN
        period_id := OLD.fiscal_period_id;
    ELSE
        period_id := NEW.fiscal_period_id;
    END IF;

    SELECT period_status INTO period_state
      FROM fiscal_periods
     WHERE tenant_id = CASE WHEN TG_OP = 'DELETE' THEN OLD.tenant_id ELSE NEW.tenant_id END
       AND company_id = CASE WHEN TG_OP = 'DELETE' THEN OLD.company_id ELSE NEW.company_id END
       AND id = period_id
     FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Fiscal period not found for journal';
    END IF;

    IF period_state <> 'OPEN' THEN
        RAISE EXCEPTION 'Journal changes are forbidden because the fiscal period is not open';
    END IF;

    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_guard_journal_period_open
BEFORE INSERT OR UPDATE OR DELETE ON journal_entries
FOR EACH ROW EXECUTE FUNCTION guard_journal_period_open();

COMMIT;
