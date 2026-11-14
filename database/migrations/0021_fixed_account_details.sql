-- Separate master data for fixed analytical details attached to a specific posting account.
-- Journal-line assignment is stored independently from floating dimensions.
BEGIN;

CREATE TABLE fixed_account_details (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    account_id uuid NOT NULL,
    detail_code varchar(60) NOT NULL,
    detail_name varchar(250) NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_fixed_detail_account
        FOREIGN KEY (tenant_id, company_id, account_id)
        REFERENCES accounts(tenant_id, company_id, id),
    CONSTRAINT uq_fixed_detail_tenant_company_id
        UNIQUE (tenant_id, company_id, id),
    CONSTRAINT uq_fixed_detail_account_id
        UNIQUE (tenant_id, company_id, account_id, id),
    CONSTRAINT uq_fixed_detail_account_code
        UNIQUE (tenant_id, company_id, account_id, detail_code),
    CONSTRAINT ck_fixed_detail_code_nonblank CHECK (length(btrim(detail_code)) > 0),
    CONSTRAINT ck_fixed_detail_name_nonblank CHECK (length(btrim(detail_name)) > 0)
);

CREATE OR REPLACE FUNCTION validate_fixed_detail_account()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    v_detail_mode varchar(10);
    v_postable boolean;
BEGIN
    SELECT detail_mode, is_postable
      INTO v_detail_mode, v_postable
      FROM accounts
     WHERE tenant_id = NEW.tenant_id
       AND company_id = NEW.company_id
       AND id = NEW.account_id
     FOR KEY SHARE;

    IF NOT FOUND OR v_detail_mode <> 'FIXED' OR NOT v_postable THEN
        RAISE EXCEPTION 'Fixed detail must belong to an active posting account configured for FIXED detail mode';
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_validate_fixed_detail_account
BEFORE INSERT OR UPDATE OF tenant_id, company_id, account_id
ON fixed_account_details
FOR EACH ROW
EXECUTE FUNCTION validate_fixed_detail_account();

CREATE OR REPLACE FUNCTION protect_account_fixed_detail_mode()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    IF EXISTS (
        SELECT 1 FROM fixed_account_details
         WHERE tenant_id = OLD.tenant_id
           AND company_id = OLD.company_id
           AND account_id = OLD.id
    ) AND (NEW.detail_mode <> 'FIXED' OR NOT NEW.is_postable) THEN
        RAISE EXCEPTION 'An account with fixed details must remain a posting account in FIXED detail mode';
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_protect_account_fixed_detail_mode
BEFORE UPDATE OF detail_mode, is_postable
ON accounts
FOR EACH ROW
EXECUTE FUNCTION protect_account_fixed_detail_mode();

CREATE TABLE journal_line_fixed_details (
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    journal_line_id uuid NOT NULL,
    fixed_detail_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, journal_line_id),
    CONSTRAINT fk_line_fixed_detail_line
        FOREIGN KEY (tenant_id, company_id, journal_line_id)
        REFERENCES journal_lines(tenant_id, company_id, id),
    CONSTRAINT fk_line_fixed_detail_detail
        FOREIGN KEY (tenant_id, company_id, fixed_detail_id)
        REFERENCES fixed_account_details(tenant_id, company_id, id)
);

CREATE OR REPLACE FUNCTION validate_journal_line_fixed_detail()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    v_line_account uuid;
    v_detail_account uuid;
BEGIN
    SELECT account_id INTO v_line_account
      FROM journal_lines
     WHERE tenant_id = NEW.tenant_id
       AND company_id = NEW.company_id
       AND id = NEW.journal_line_id;

    SELECT account_id INTO v_detail_account
      FROM fixed_account_details
     WHERE tenant_id = NEW.tenant_id
       AND company_id = NEW.company_id
       AND id = NEW.fixed_detail_id
       AND is_active;

    IF NOT FOUND OR v_line_account IS DISTINCT FROM v_detail_account THEN
        RAISE EXCEPTION 'Journal line fixed detail must be active and belong to the exact same account';
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_validate_journal_line_fixed_detail
BEFORE INSERT OR UPDATE
ON journal_line_fixed_details
FOR EACH ROW
EXECUTE FUNCTION validate_journal_line_fixed_detail();

ALTER TABLE fixed_account_details ENABLE ROW LEVEL SECURITY;
ALTER TABLE journal_line_fixed_details ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_fixed_details ON fixed_account_details
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

CREATE POLICY tenant_isolation_line_fixed_details ON journal_line_fixed_details
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

CREATE INDEX ix_fixed_details_account_active
    ON fixed_account_details(tenant_id, company_id, account_id, is_active, detail_code);

COMMIT;
