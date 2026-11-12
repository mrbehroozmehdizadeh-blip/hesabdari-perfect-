-- Transactional smoke tests for ledger provenance, immutability and complete posting.
-- Runs only against the fresh ephemeral CI database and rolls back all fixtures.
BEGIN;

DO $test$
DECLARE
    v_tenant uuid := gen_random_uuid();
    v_company uuid := gen_random_uuid();
    v_period uuid := gen_random_uuid();
    v_creator uuid := gen_random_uuid();
    v_approver uuid := gen_random_uuid();
    v_poster uuid := gen_random_uuid();
    v_account_debit uuid := gen_random_uuid();
    v_account_credit uuid := gen_random_uuid();
    v_journal uuid := gen_random_uuid();
    v_line_debit uuid := gen_random_uuid();
    v_line_credit uuid := gen_random_uuid();
    v_gl_line uuid;
    v_second_journal uuid := gen_random_uuid();
    v_second_line_a uuid := gen_random_uuid();
    v_second_line_b uuid := gen_random_uuid();
    v_bad_reversal uuid := gen_random_uuid();
    v_bad_reversal_line_a uuid := gen_random_uuid();
    v_bad_reversal_line_b uuid := gen_random_uuid();
    v_rejected boolean;
BEGIN
    INSERT INTO tenants(id,tenant_code,display_name)
    VALUES(v_tenant,'LEDGER-CI-' || replace(v_tenant::text,'-',''),'Ledger CI');
    INSERT INTO companies(id,tenant_id,company_code,legal_name,functional_currency)
    VALUES(v_company,v_tenant,'CI','Ledger CI Company','USD');
    INSERT INTO fiscal_periods(id,tenant_id,company_id,period_code,starts_on,ends_on)
    VALUES(v_period,v_tenant,v_company,'FY-CI',DATE '2026-01-01',DATE '2026-12-31');

    INSERT INTO accounts(id,tenant_id,company_id,account_code,account_name,account_level,
                         account_class,normal_balance,is_postable)
    VALUES
      (v_account_debit,v_tenant,v_company,'1000','Cash',1,'ASSET','DEBIT',true),
      (v_account_credit,v_tenant,v_company,'4000','Revenue',1,'REVENUE','CREDIT',true);

    PERFORM set_config('app.tenant_id',v_tenant::text,true);

    INSERT INTO journal_entries(id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,
                                currency_code,entry_status,description,idempotency_key,created_by)
    VALUES(v_journal,v_tenant,v_company,v_period,'CI-POST-1',DATE '2026-01-02',
           'USD','DRAFT','Ledger CI posting','ci-post-1',v_creator);

    INSERT INTO journal_lines(id,tenant_id,company_id,journal_entry_id,line_number,account_id,debit_amount,credit_amount)
    VALUES
      (v_line_debit,v_tenant,v_company,v_journal,1,v_account_debit,100,0),
      (v_line_credit,v_tenant,v_company,v_journal,2,v_account_credit,0,100);

    UPDATE journal_entries
       SET entry_status='PENDING',submitted_by=v_creator,submitted_at=now()
     WHERE id=v_journal;
    UPDATE journal_entries
       SET entry_status='APPROVED',approved_by=v_approver,approved_at=now()
     WHERE id=v_journal;

    INSERT INTO general_ledger_lines
        (tenant_id,company_id,fiscal_period_id,journal_entry_id,journal_line_id,
         account_id,entry_date,currency_code,debit_amount,credit_amount,posted_by)
    SELECT l.tenant_id,l.company_id,v_period,j.id,l.id,l.account_id,j.entry_date,
           j.currency_code,l.debit_amount,l.credit_amount,v_poster
      FROM journal_lines l JOIN journal_entries j
        ON j.tenant_id=l.tenant_id AND j.company_id=l.company_id AND j.id=l.journal_entry_id
     WHERE j.id=v_journal;

    SELECT id INTO v_gl_line FROM general_ledger_lines WHERE journal_line_id=v_line_debit;

    UPDATE journal_entries SET entry_status='POSTED',posted_by=v_poster,posted_at=now()
     WHERE id=v_journal;

    v_rejected := false;
    BEGIN
        UPDATE general_ledger_lines SET debit_amount=101 WHERE id=v_gl_line;
    EXCEPTION WHEN OTHERS THEN
        v_rejected := true;
    END;
    IF NOT v_rejected THEN
        RAISE EXCEPTION 'Ledger immutability test failed: UPDATE unexpectedly succeeded';
    END IF;

    v_rejected := false;
    BEGIN
        UPDATE journal_entries SET description='tampered' WHERE id=v_journal;
    EXCEPTION WHEN OTHERS THEN
        v_rejected := true;
    END;
    IF NOT v_rejected THEN
        RAISE EXCEPTION 'Posted journal immutability test failed: UPDATE unexpectedly succeeded';
    END IF;

    -- An approved journal with one missing ledger row must not become POSTED.
    INSERT INTO journal_entries(id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,
                                currency_code,entry_status,description,idempotency_key,created_by)
    VALUES(v_second_journal,v_tenant,v_company,v_period,'CI-POST-INCOMPLETE',DATE '2026-01-03',
           'USD','DRAFT','Incomplete ledger test','ci-post-incomplete',v_creator);
    INSERT INTO journal_lines(id,tenant_id,company_id,journal_entry_id,line_number,account_id,debit_amount,credit_amount)
    VALUES
      (v_second_line_a,v_tenant,v_company,v_second_journal,1,v_account_debit,50,0),
      (v_second_line_b,v_tenant,v_company,v_second_journal,2,v_account_credit,0,50);
    UPDATE journal_entries
       SET entry_status='PENDING',submitted_by=v_creator,submitted_at=now()
     WHERE id=v_second_journal;
    UPDATE journal_entries
       SET entry_status='APPROVED',approved_by=v_approver,approved_at=now()
     WHERE id=v_second_journal;

    INSERT INTO general_ledger_lines
        (tenant_id,company_id,fiscal_period_id,journal_entry_id,journal_line_id,
         account_id,entry_date,currency_code,debit_amount,credit_amount,posted_by)
    SELECT l.tenant_id,l.company_id,v_period,j.id,l.id,l.account_id,j.entry_date,
           j.currency_code,l.debit_amount,l.credit_amount,v_poster
      FROM journal_lines l JOIN journal_entries j
        ON j.tenant_id=l.tenant_id AND j.company_id=l.company_id AND j.id=l.journal_entry_id
     WHERE j.id=v_second_journal AND l.id=v_second_line_a;

    v_rejected := false;
    BEGIN
        UPDATE journal_entries SET entry_status='POSTED',posted_by=v_poster,posted_at=now()
         WHERE id=v_second_journal;
    EXCEPTION WHEN OTHERS THEN
        v_rejected := true;
    END;
    IF NOT v_rejected THEN
        RAISE EXCEPTION 'Posting completeness test failed: incomplete journal unexpectedly posted';
    END IF;

    -- The database must reject a reversal whose lines do not exactly invert its source.
    INSERT INTO journal_entries(id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,
                                currency_code,entry_status,description,idempotency_key,created_by,
                                reversal_of_journal_id)
    VALUES(v_bad_reversal,v_tenant,v_company,v_period,'CI-BAD-REVERSAL',DATE '2026-01-04',
           'USD','DRAFT','Bad reversal','ci-bad-reversal',v_creator,v_journal);
    INSERT INTO journal_lines(id,tenant_id,company_id,journal_entry_id,line_number,account_id,debit_amount,credit_amount)
    VALUES
      (v_bad_reversal_line_a,v_tenant,v_company,v_bad_reversal,1,v_account_debit,1,0),
      (v_bad_reversal_line_b,v_tenant,v_company,v_bad_reversal,2,v_account_credit,0,1);
    UPDATE journal_entries
       SET entry_status='PENDING',submitted_by=v_creator,submitted_at=now()
     WHERE id=v_bad_reversal;
    v_rejected := false;
    BEGIN
        UPDATE journal_entries
           SET entry_status='APPROVED',approved_by=v_approver,approved_at=now()
         WHERE id=v_bad_reversal;
    EXCEPTION WHEN OTHERS THEN
        v_rejected := true;
    END;
    IF NOT v_rejected THEN
        RAISE EXCEPTION 'Reversal integrity test failed: incorrect reversal lines unexpectedly approved';
    END IF;

    -- Even a complete ledger cannot be finalized by the creator.
    INSERT INTO general_ledger_lines
        (tenant_id,company_id,fiscal_period_id,journal_entry_id,journal_line_id,
         account_id,entry_date,currency_code,debit_amount,credit_amount,posted_by)
    SELECT l.tenant_id,l.company_id,v_period,j.id,l.id,l.account_id,j.entry_date,
           j.currency_code,l.debit_amount,l.credit_amount,v_creator
      FROM journal_lines l JOIN journal_entries j
        ON j.tenant_id=l.tenant_id AND j.company_id=l.company_id AND j.id=l.journal_entry_id
     WHERE j.id=v_second_journal AND l.id=v_second_line_b;

    v_rejected := false;
    BEGIN
        UPDATE journal_entries SET entry_status='POSTED',posted_by=v_creator,posted_at=now()
         WHERE id=v_second_journal;
    EXCEPTION WHEN OTHERS THEN
        v_rejected := true;
    END;
    IF NOT v_rejected THEN
        RAISE EXCEPTION 'Posting separation test failed: creator unexpectedly posted their own journal';
    END IF;
END;
$test$;

ROLLBACK;
