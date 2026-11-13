-- Do not allow role-change requests to create or target unverified company identities.
-- A target must already be an active member of the same tenant and company.
BEGIN;

CREATE OR REPLACE FUNCTION require_active_membership_change_target()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    IF NOT EXISTS (
        SELECT 1
          FROM company_memberships m
         WHERE m.tenant_id = NEW.tenant_id
           AND m.company_id = NEW.company_id
           AND m.user_id = NEW.target_user_id
           AND m.is_active = true
    ) THEN
        RAISE EXCEPTION 'Membership role changes require an existing active company membership'
            USING ERRCODE = '23503';
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_membership_change_requires_active_target
BEFORE INSERT ON membership_change_requests
FOR EACH ROW
EXECUTE FUNCTION require_active_membership_change_target();

COMMIT;
