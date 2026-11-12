-- Strong database-side provenance checks for append-only ledger rows.
BEGIN;

CREATE OR REPLACE FUNCTION validate_general_ledger_insert()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    journal_status varchar(12);
    source_period uuid;
    source_date date;
    source_currency char(3);
    source_account uuid;
    source_debit numeric(28,8);
    source_credit numeric(28,8);
BEGIN
    SELECT j.entry_status,j.fiscal_period_id,j.entry_date,j.currency_code,
           l.account_id,l.debit_amount,l.credit_amount
      INTO journal_status,source_period,source_date,source_currency,
           source_account,source_debit,source_credit
      FROM journal_entries j
      JOIN journal_lines l
        ON l.tenant_id=j.tenant_id AND l.company_id=j.company_id
       AND l.journal_entry_id=j.id
     WHERE j.tenant_id=NEW.tenant_id
       AND j.company_id=NEW.company_id
       AND j.id=NEW.journal_entry_id
       AND l.id=NEW.journal_line_id
     FOR UPDATE OF j;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Ledger row must reference an existing source journal line in the same company';
    END IF;

    IF journal_status <> 'APPROVED' THEN
        RAISE EXCEPTION 'Only an approved journal may create general-ledger rows';
    END IF;

    IF NEW.fiscal_period_id <> source_period OR NEW.entry_date <> source_date
       OR NEW.currency_code <> source_currency OR NEW.account_id <> source_account
       OR NEW.debit_amount <> source_debit OR NEW.credit_amount <> source_credit
       OR NEW.posted_by IS NULL THEN
        RAISE EXCEPTION 'Ledger row must exactly match its approved source journal line';
    END IF;

    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_validate_general_ledger_insert
BEFORE INSERT ON general_ledger_lines
FOR EACH ROW EXECUTE FUNCTION validate_general_ledger_insert();

CREATE OR REPLACE FUNCTION guard_journal_lifecycle()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    ledger_count bigint;
    source_line_count bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.entry_status <> 'DRAFT' THEN
            RAISE EXCEPTION 'Only draft journal entries may be deleted';
        END IF;
        RETURN OLD;
    END IF;

    IF OLD.entry_status IN ('POSTED','REVERSED') THEN
        RAISE EXCEPTION 'Posted or reversed journal entries are immutable; use a reversal journal';
    END IF;

    IF NEW.entry_status <> OLD.entry_status AND NOT (
        (OLD.entry_status = 'DRAFT' AND NEW.entry_status = 'PENDING') OR
        (OLD.entry_status = 'PENDING' AND NEW.entry_status = 'APPROVED') OR
        (OLD.entry_status = 'APPROVED' AND NEW.entry_status = 'POSTED')
    ) THEN
        RAISE EXCEPTION 'Invalid journal status transition: % -> %', OLD.entry_status, NEW.entry_status;
    END IF;

    IF NEW.entry_status = 'PENDING' AND
       (NEW.submitted_by IS NULL OR NEW.submitted_at IS NULL) THEN
        RAISE EXCEPTION 'Submitting a journal requires submitted_by and submitted_at';
    END IF;

    IF NEW.entry_status IN ('APPROVED','POSTED') AND
       (NEW.approved_by IS NULL OR NEW.approved_at IS NULL OR NEW.approved_by = NEW.created_by) THEN
        RAISE EXCEPTION 'Approval requires a distinct approver and approval timestamp';
    END IF;

    IF NEW.entry_status = 'POSTED' THEN
        IF NEW.posted_by IS NULL OR NEW.posted_at IS NULL THEN
            RAISE EXCEPTION 'Posting requires posted_by and posted_at';
        END IF;

        SELECT count(*) INTO source_line_count
          FROM journal_lines
         WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id
           AND journal_entry_id=NEW.id;
        SELECT count(*) INTO ledger_count
          FROM general_ledger_lines
         WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id
           AND journal_entry_id=NEW.id;

        IF source_line_count < 2 OR ledger_count <> source_line_count THEN
            RAISE EXCEPTION 'A journal cannot be posted unless every source line exists in the general ledger';
        END IF;
    END IF;

    RETURN NEW;
END;
$function$;

COMMIT;
