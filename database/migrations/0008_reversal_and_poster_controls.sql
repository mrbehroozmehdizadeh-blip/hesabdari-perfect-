-- Enforce posting actor separation and exact reversal lineage at the database boundary.
BEGIN;

CREATE OR REPLACE FUNCTION validate_reversal_approval()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    source_status varchar(12);
    source_count bigint;
    reversal_count bigint;
BEGIN
    IF NEW.entry_status='APPROVED' AND NEW.reversal_of_journal_id IS NOT NULL THEN
        SELECT entry_status INTO source_status
          FROM journal_entries
         WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id
           AND id=NEW.reversal_of_journal_id
         FOR SHARE;

        IF NOT FOUND OR source_status <> 'POSTED' THEN
            RAISE EXCEPTION 'A reversal can be approved only while its source journal remains posted';
        END IF;

        SELECT count(*) INTO source_count
          FROM journal_lines
         WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id
           AND journal_entry_id=NEW.reversal_of_journal_id;
        SELECT count(*) INTO reversal_count
          FROM journal_lines
         WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id
           AND journal_entry_id=NEW.id;

        IF source_count < 2 OR reversal_count <> source_count OR EXISTS (
            SELECT 1
              FROM journal_lines r
              LEFT JOIN journal_lines s
                ON s.tenant_id=r.tenant_id AND s.company_id=r.company_id
               AND s.journal_entry_id=NEW.reversal_of_journal_id
               AND s.line_number=r.line_number
             WHERE r.tenant_id=NEW.tenant_id AND r.company_id=NEW.company_id
               AND r.journal_entry_id=NEW.id
               AND (s.id IS NULL OR r.account_id<>s.account_id
                    OR r.debit_amount<>s.credit_amount OR r.credit_amount<>s.debit_amount)
        ) THEN
            RAISE EXCEPTION 'Reversal lines must exactly mirror source lines with debit and credit exchanged';
        END IF;
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_validate_reversal_approval
BEFORE UPDATE OF entry_status ON journal_entries
FOR EACH ROW EXECUTE FUNCTION validate_reversal_approval();

CREATE OR REPLACE FUNCTION guard_journal_lifecycle()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    ledger_count bigint;
    source_line_count bigint;
    actor_line_count bigint;
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

    IF NEW.reversal_of_journal_id IS DISTINCT FROM OLD.reversal_of_journal_id THEN
        RAISE EXCEPTION 'The source link of a reversal journal is immutable';
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
        IF NEW.posted_by IS NULL OR NEW.posted_at IS NULL
           OR NEW.posted_by=NEW.created_by OR NEW.posted_by=NEW.approved_by THEN
            RAISE EXCEPTION 'Posting requires a distinct poster and posting timestamp';
        END IF;

        SELECT count(*) INTO source_line_count
          FROM journal_lines
         WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id
           AND journal_entry_id=NEW.id;
        SELECT count(*),count(*) FILTER (WHERE posted_by=NEW.posted_by)
          INTO ledger_count,actor_line_count
          FROM general_ledger_lines
         WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id
           AND journal_entry_id=NEW.id;

        IF source_line_count < 2 OR ledger_count <> source_line_count
           OR actor_line_count <> source_line_count THEN
            RAISE EXCEPTION 'Every source line must be posted by the authorized posting actor before journal finalization';
        END IF;
    END IF;

    RETURN NEW;
END;
$function$;

COMMIT;
