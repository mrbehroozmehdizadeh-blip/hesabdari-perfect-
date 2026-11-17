-- Revalidate analytical master-data activity at every journal workflow transition.
BEGIN;

CREATE OR REPLACE FUNCTION enforce_journal_line_analytics_before_progress()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    v_invalid bigint;
BEGIN
    IF NEW.entry_status IN ('PENDING','APPROVED','POSTED')
       AND OLD.entry_status IS DISTINCT FROM NEW.entry_status THEN
        SELECT count(*) INTO v_invalid
          FROM journal_lines l
          JOIN accounts a
            ON a.tenant_id=l.tenant_id AND a.company_id=l.company_id AND a.id=l.account_id
         WHERE l.tenant_id=NEW.tenant_id AND l.company_id=NEW.company_id
           AND l.journal_entry_id=NEW.id
           AND (
             (a.detail_mode='FIXED' AND (
                 (SELECT count(*) FROM journal_line_fixed_details f
                   JOIN fixed_account_details d
                     ON d.tenant_id=f.tenant_id AND d.company_id=f.company_id AND d.id=f.fixed_detail_id
                  WHERE f.tenant_id=l.tenant_id AND f.company_id=l.company_id
                    AND f.journal_line_id=l.id AND d.is_active=true) <> 1
                 OR EXISTS (SELECT 1 FROM journal_line_dimensions x
                             WHERE x.tenant_id=l.tenant_id AND x.company_id=l.company_id AND x.journal_line_id=l.id)
             ))
             OR
             (a.detail_mode='FLOATING' AND (
                 NOT EXISTS (
                     SELECT 1 FROM journal_line_dimensions x
                     JOIN floating_dimensions d
                       ON d.tenant_id=x.tenant_id AND d.company_id=x.company_id AND d.id=x.dimension_id
                    WHERE x.tenant_id=l.tenant_id AND x.company_id=l.company_id
                      AND x.journal_line_id=l.id AND d.is_active=true
                 )
                 OR EXISTS (
                     SELECT 1 FROM journal_line_dimensions x
                     JOIN floating_dimensions d
                       ON d.tenant_id=x.tenant_id AND d.company_id=x.company_id AND d.id=x.dimension_id
                    WHERE x.tenant_id=l.tenant_id AND x.company_id=l.company_id
                      AND x.journal_line_id=l.id AND d.is_active=false
                 )
                 OR EXISTS (SELECT 1 FROM journal_line_fixed_details f
                             WHERE f.tenant_id=l.tenant_id AND f.company_id=l.company_id AND f.journal_line_id=l.id)
             ))
             OR
             (a.detail_mode='NONE' AND (
                 EXISTS (SELECT 1 FROM journal_line_fixed_details f
                          WHERE f.tenant_id=l.tenant_id AND f.company_id=l.company_id AND f.journal_line_id=l.id)
                 OR EXISTS (SELECT 1 FROM journal_line_dimensions x
                             WHERE x.tenant_id=l.tenant_id AND x.company_id=l.company_id AND x.journal_line_id=l.id)
             ))
           );
        IF v_invalid > 0 THEN
            RAISE EXCEPTION 'Journal lines have missing, inactive or incompatible analytical details (% lines)', v_invalid
                USING ERRCODE='23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$function$;

COMMIT;
