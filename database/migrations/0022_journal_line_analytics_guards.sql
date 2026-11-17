-- Enforce mutually exclusive fixed/floating analytics and require them before workflow progression.
BEGIN;

CREATE OR REPLACE FUNCTION validate_journal_line_floating_dimension()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    v_detail_mode varchar(10);
BEGIN
    SELECT detail_mode INTO v_detail_mode
      FROM accounts
     WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id
       AND id=(SELECT account_id FROM journal_lines
                WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id AND id=NEW.journal_line_id);
    IF v_detail_mode IS DISTINCT FROM 'FLOATING' THEN
        RAISE EXCEPTION 'Floating dimensions can only be assigned to FLOATING-mode posting accounts';
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_validate_journal_line_floating_dimension
BEFORE INSERT OR UPDATE ON journal_line_dimensions
FOR EACH ROW EXECUTE FUNCTION validate_journal_line_floating_dimension();

CREATE OR REPLACE FUNCTION validate_journal_line_fixed_detail_mode()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    v_detail_mode varchar(10);
BEGIN
    SELECT a.detail_mode INTO v_detail_mode
      FROM journal_lines l
      JOIN accounts a ON a.tenant_id=l.tenant_id AND a.company_id=l.company_id AND a.id=l.account_id
     WHERE l.tenant_id=NEW.tenant_id AND l.company_id=NEW.company_id AND l.id=NEW.journal_line_id;
    IF v_detail_mode IS DISTINCT FROM 'FIXED' THEN
        RAISE EXCEPTION 'Fixed details can only be assigned to FIXED-mode posting accounts';
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_validate_journal_line_fixed_detail_mode
BEFORE INSERT OR UPDATE ON journal_line_fixed_details
FOR EACH ROW EXECUTE FUNCTION validate_journal_line_fixed_detail_mode();

CREATE OR REPLACE FUNCTION enforce_journal_line_analytics_before_progress()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    v_missing bigint;
BEGIN
    IF NEW.entry_status IN ('PENDING','APPROVED','POSTED') AND OLD.entry_status IS DISTINCT FROM NEW.entry_status THEN
        SELECT count(*) INTO v_missing
          FROM journal_lines l
          JOIN accounts a
            ON a.tenant_id=l.tenant_id AND a.company_id=l.company_id AND a.id=l.account_id
         WHERE l.tenant_id=NEW.tenant_id AND l.company_id=NEW.company_id
           AND l.journal_entry_id=NEW.id
           AND (
             (a.detail_mode='FIXED' AND (
                 (SELECT count(*) FROM journal_line_fixed_details f
                   WHERE f.tenant_id=l.tenant_id AND f.company_id=l.company_id AND f.journal_line_id=l.id) <> 1
                 OR EXISTS (SELECT 1 FROM journal_line_dimensions d
                             WHERE d.tenant_id=l.tenant_id AND d.company_id=l.company_id AND d.journal_line_id=l.id)
             ))
             OR
             (a.detail_mode='FLOATING' AND (
                 NOT EXISTS (SELECT 1 FROM journal_line_dimensions d
                              WHERE d.tenant_id=l.tenant_id AND d.company_id=l.company_id AND d.journal_line_id=l.id)
                 OR EXISTS (SELECT 1 FROM journal_line_fixed_details f
                             WHERE f.tenant_id=l.tenant_id AND f.company_id=l.company_id AND f.journal_line_id=l.id)
             ))
             OR
             (a.detail_mode='NONE' AND (
                 EXISTS (SELECT 1 FROM journal_line_fixed_details f
                          WHERE f.tenant_id=l.tenant_id AND f.company_id=l.company_id AND f.journal_line_id=l.id)
                 OR EXISTS (SELECT 1 FROM journal_line_dimensions d
                             WHERE d.tenant_id=l.tenant_id AND d.company_id=l.company_id AND d.journal_line_id=l.id)
             ))
           );
        IF v_missing > 0 THEN
            RAISE EXCEPTION 'Journal lines have missing or incompatible analytical details (% lines)', v_missing
                USING ERRCODE='23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_enforce_journal_line_analytics_before_progress
BEFORE UPDATE OF entry_status ON journal_entries
FOR EACH ROW EXECUTE FUNCTION enforce_journal_line_analytics_before_progress();

COMMIT;
