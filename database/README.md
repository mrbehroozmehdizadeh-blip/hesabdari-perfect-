# Database foundation

The initial schema is in `migrations/0001_financial_core.sql`; company membership authorization is added by `migrations/0002_company_memberships.sql`, journal-line company integrity by `migrations/0003_journal_line_company_integrity.sql`, journal idempotency fingerprints by `migrations/0004_journal_idempotency_fingerprint.sql`, approval lifecycle / immutability guards by `migrations/0005_journal_approval_lifecycle.sql`, append-only general-ledger storage by `migrations/0006_general_ledger.sql`, and database-side ledger provenance/completeness guards by `migrations/0007_ledger_provenance_guards.sql`, reversal lineage / distinct posting-actor enforcement by `migrations/0008_reversal_and_poster_controls.sql`, fiscal-period closure / opening-journal classification by `migrations/0009_fiscal_period_controls.sql`, and year-end closing safeguards by `migrations/0010_year_end_closing_guards.sql`. Apply them in numeric order to a new development database.

## Safety and execution status
- This migration set is a design baseline for a NEW development database.
- It has NOT been applied to Floot or to any production database.
- All nine migrations and the rollback-only invariant suites are executed in CI against a fresh, ephemeral PostgreSQL 18 service database. This validates schema creation but is not a backup/restore rehearsal or a production deployment.
- Do not run it against existing data until a backup, restore rehearsal, schema comparison and explicit migration plan exist.

## Tenant isolation
The application must set tenant context inside every database transaction before querying tenant-scoped tables:

```sql
BEGIN;
SET LOCAL app.tenant_id = '00000000-0000-0000-0000-000000000000';
-- tenant-scoped statements
COMMIT;
```

Use a non-owner application database role without BYPASSRLS. The table owner and superusers can bypass row-level security unless FORCE ROW LEVEL SECURITY is enabled; production role design and FORCE RLS policy are a required security gate before deployment.

## Important domain boundaries
- Account tree depth is capped at ten levels. A PostgreSQL trigger validates that each child has an immediate parent exactly one level above and that a posting account cannot be a parent.
- Account deletion must be evaluated in a consistent transaction and rejected when child accounts, a non-zero balance, or transaction references exist. Foreign keys provide an additional guard for referenced rows.
- Fixed and floating details are modeled separately; floating dimensions are not additional account-tree levels.
- Monetary values use PostgreSQL numeric(28,8), never binary floating point.
- A single journal entry uses one currency in this initial schema. The current draft API restricts the currency to the company's functional currency until explicit FX conversion and valuation workflows are implemented.
- Journal draft creation checks company membership, open fiscal period/date, active posting accounts, exact debit/credit equality, and writes the header, lines, and audit event in one transaction.
- The draft endpoint uses an idempotency key plus a SHA-256 request fingerprint; reusing a key for different content is rejected.
- Draft creation is separate from approval and posting. Submission freezes journal lines; approval requires a distinct user with the explicit `FINANCE_APPROVER` membership role and rechecks balance, account validity, and fiscal-period status.
- The API supports draft retrieval, creator-only submission, independent approval, and a separate posting permission (`FINANCE_POSTER`). Posting rechecks approval, maker-checker separation, open period, account validity and debit/credit balance.
- Database triggers enforce the permitted draft → pending → approved → posted state transitions and prevent journal-header mutation after posting. Journal lines become immutable as soon as a draft is submitted.
- Posting writes immutable general-ledger rows, updates the source journal to POSTED, and writes the audit event in one database transaction. A unique source-line constraint prevents duplicate ledger posting; update/delete triggers make ledger rows append-only.
- The general ledger is the accounting source of truth for posted movements. Read APIs now provide a period-specific trial balance and up to 500 chronological account-activity rows with running debit-net balance. Account balances and trial balance are derived from ledger rows; the journal header alone is not a posted balance.
- A reversal endpoint creates a new DRAFT in an open target period, copies the original posted lines with debit/credit swapped, links the new document to its source, and subjects it to the same submit/approve/post workflow. The original posted document is never edited. One reversal document per source is enforced.
- Database trigger validation requires every ledger row to match its approved source line exactly, and the POSTED transition is rejected unless all source lines have corresponding ledger rows posted by the same distinct posting actor. A reversal can be approved only if its lines exactly mirror its still-posted source document with debit and credit exchanged. `database/tests/ledger_invariants.sql` exercises successful posting, immutable posted rows/headers, and rejection of incomplete posting in a rollback-only CI transaction.
- The ledger account activity endpoint supports optional `fromDate`, `toDate`, and `fiscalPeriodId` filters plus bounded `page` / `pageSize` pagination. Running debit-net balance is calculated before the lower-date filter, so a date-window report retains the account's accumulated balance rather than restarting at zero. The response includes total row count and `hasMore`.
- A controlled opening-balance draft endpoint is available at `POST /api/companies/{companyId}/opening-balances`. It requires the `FINANCE_OPENING_MANAGER` company role, uses the first day of the target period and the company's functional currency, validates unique active/postable accounts and exact debit-credit equality, and creates a normal maker-checker DRAFT classified as `OPENING`. It is idempotent by request key and writes an audit event. The created journal then uses the existing submit → approve → post workflow; the creator cannot approve their own entry. Carry-forward automation, opening balance reconciliation against prior-year closing, and a user-facing form remain unimplemented.
- Fiscal periods now have database-enforced OPEN → CLOSED → LOCKED transitions. Migration 0010 also enforces at most one closing journal per period and rejects period closure while any revenue or expense account retains a non-zero net balance in the general ledger. A closing journal must therefore pass the ordinary submit → approve → post controls before the period can close. Closure is rejected while any DRAFT, PENDING, or APPROVED journal remains; reopening is not permitted. Journal header mutations serialize on the fiscal-period row and are rejected unless the period is OPEN, preventing a close-versus-create race. A journal classification column and a unique one-opening-journal-per-period index establish the database foundation for a controlled opening-balance workflow; the end-user opening-balance API and carry-forward process are not yet implemented.
- FX valuation, fine-grained RBAC administration, production role hardening, and audit completeness remain required controls before production.
- Every tenant-scoped query must use the authenticated tenant context; never accept tenant identity from an untrusted request body as authorization.
- Company-scoped API access additionally requires an active `company_memberships` row for the authenticated user, tenant, and company. Membership provisioning is administrative; `role_code` is reserved for upcoming fine-grained RBAC enforcement.
