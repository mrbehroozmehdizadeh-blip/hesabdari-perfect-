-- Journal lifecycle metadata and database-enforced transition / immutability guards.
BEGIN;

ALTER TABLE journal_entries
    ADD COLUMN submitted_by uuid NULL,
    ADD COLUMN submitted_at timestamptz NULL,
    ADD COLUMN approved_at timestamptz NULL;

ALTER TABLE journal_entries
    ADD CONSTRAINT ck_journal_submission_metadata
    CHECK (
        (entry_status = 'DRAFT' AND submitted_by IS NULL AND submitted_at IS NULL AND approved_by IS NULL AND approved_at IS NULL)
        OR
        (entry_status IN ('PENDING','APPROVED','POSTED','REVERSED') AND submitted_by IS NOT NULL AND submitted_at IS NOT NULL)
    );

ALTER TABLE journal_entries
    ADD CONSTRAINT ck_journal_approval_metadata
    CHECK (
        (entry_status IN ('DRAFT','PENDING') AND approved_by IS NULL AND approved_at IS NULL)
        OR
        (entry_status IN ('APPROVED','POSTED','REVERSED') AND approved_by IS NOT NULL AND approved_at IS NOT NULL)
    );

ALTER TABLE journal_entries
    ADD CONSTRAINT ck_journal_maker_checker
    CHECK (approved_by IS NULL OR approved_by <> created_by);

CREATE OR REPLACE FUNCTION guard_journal_lifecycle()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
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

    IF NEW.entry_status = 'POSTED' AND (NEW.posted_by IS NULL OR NEW.posted_at IS NULL) THEN
        RAISE EXCEPTION 'Posting requires posted_by and posted_at';
    END IF;

    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_guard_journal_lifecycle
BEFORE UPDATE OR DELETE ON journal_entries
FOR EACH ROW EXECUTE FUNCTION guard_journal_lifecycle();

CREATE OR REPLACE FUNCTION guard_journal_line_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    parent_status varchar(12);
    parent_tenant uuid;
    parent_company uuid;
    parent_id uuid;
BEGIN
    IF TG_OP = 'DELETE' THEN
        parent_tenant := OLD.tenant_id;
        parent_company := OLD.company_id;
        parent_id := OLD.journal_entry_id;
    ELSE
        parent_tenant := NEW.tenant_id;
        parent_company := NEW.company_id;
        parent_id := NEW.journal_entry_id;
    END IF;

    SELECT entry_status INTO parent_status
      FROM journal_entries
     WHERE tenant_id = parent_tenant
       AND company_id = parent_company
       AND id = parent_id
     FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Journal header not found for journal line';
    END IF;

    IF parent_status <> 'DRAFT' THEN
        RAISE EXCEPTION 'Journal lines are immutable after submission for approval';
    END IF;

    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_guard_journal_line_mutation
BEFORE INSERT OR UPDATE OR DELETE ON journal_lines
FOR EACH ROW EXECUTE FUNCTION guard_journal_line_mutation();

COMMIT;
