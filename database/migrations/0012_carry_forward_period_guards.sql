-- Database-side validation of carry-forward source/target period lifecycle.
BEGIN;

CREATE OR REPLACE FUNCTION validate_opening_carry_forward_periods()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    source_end date;
    source_status varchar(12);
    target_start date;
    target_status varchar(12);
BEGIN
    IF NEW.opening_source_period_id IS NULL THEN
        RETURN NEW;
    END IF;

    IF NEW.journal_type <> 'OPENING' THEN
        RAISE EXCEPTION 'A carry-forward source period is valid only for OPENING journals';
    END IF;

    SELECT ends_on, period_status INTO source_end, source_status
      FROM fiscal_periods
     WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id
       AND id=NEW.opening_source_period_id
     FOR UPDATE;

    SELECT starts_on, period_status INTO target_start, target_status
      FROM fiscal_periods
     WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id
       AND id=NEW.fiscal_period_id
     FOR UPDATE;

    IF source_status IS DISTINCT FROM 'LOCKED' THEN
        RAISE EXCEPTION 'Carry-forward source period must be LOCKED';
    END IF;
    IF target_status IS DISTINCT FROM 'OPEN' THEN
        RAISE EXCEPTION 'Carry-forward target period must be OPEN';
    END IF;
    IF target_start IS DISTINCT FROM source_end + 1 THEN
        RAISE EXCEPTION 'Carry-forward target must begin immediately after source period end';
    END IF;

    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_validate_opening_carry_forward_periods
BEFORE INSERT OR UPDATE OF fiscal_period_id, opening_source_period_id, journal_type
ON journal_entries
FOR EACH ROW
EXECUTE FUNCTION validate_opening_carry_forward_periods();

COMMIT;
