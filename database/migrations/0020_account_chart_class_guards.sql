-- Strengthen chart-of-accounts consistency at the database boundary.
BEGIN;

ALTER TABLE accounts
    ADD CONSTRAINT ck_account_group_detail_mode
    CHECK (is_postable OR detail_mode = 'NONE');

CREATE OR REPLACE FUNCTION validate_account_hierarchy()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    parent_level smallint;
    parent_is_postable boolean;
    parent_class varchar(12);
BEGIN
    IF NEW.account_level = 1 THEN
        IF NEW.parent_account_id IS NOT NULL THEN
            RAISE EXCEPTION 'Level-one accounts cannot have a parent';
        END IF;
        RETURN NEW;
    END IF;

    SELECT account_level, is_postable, account_class
      INTO parent_level, parent_is_postable, parent_class
      FROM accounts
     WHERE tenant_id = NEW.tenant_id
       AND company_id = NEW.company_id
       AND id = NEW.parent_account_id
     FOR KEY SHARE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Parent account does not exist in the same company';
    END IF;
    IF parent_level <> NEW.account_level - 1 THEN
        RAISE EXCEPTION 'Parent account level must be exactly one less than child level';
    END IF;
    IF parent_is_postable THEN
        RAISE EXCEPTION 'A posting account cannot have child accounts';
    END IF;
    IF parent_class <> NEW.account_class THEN
        RAISE EXCEPTION 'Parent and child accounts must belong to the same account class';
    END IF;

    RETURN NEW;
END;
$function$;

CREATE OR REPLACE FUNCTION protect_account_parent_from_invalid_update()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    IF EXISTS (
        SELECT 1
          FROM accounts child
         WHERE child.tenant_id = OLD.tenant_id
           AND child.company_id = OLD.company_id
           AND child.parent_account_id = OLD.id
    ) THEN
        IF NEW.is_postable THEN
            RAISE EXCEPTION 'An account with children cannot be changed into a posting account';
        END IF;
        IF EXISTS (
            SELECT 1
              FROM accounts child
             WHERE child.tenant_id = OLD.tenant_id
               AND child.company_id = OLD.company_id
               AND child.parent_account_id = OLD.id
               AND (child.account_level <> NEW.account_level + 1
                    OR child.account_class <> NEW.account_class)
        ) THEN
            RAISE EXCEPTION 'Changing this account would invalidate child account level or class';
        END IF;
    END IF;
    RETURN NEW;
END;
$function$;

COMMIT;
