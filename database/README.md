# Database foundation

The initial schema is in `migrations/0001_financial_core.sql`; company membership authorization is added by `migrations/0002_company_memberships.sql`, journal-line company integrity by `migrations/0003_journal_line_company_integrity.sql`, and journal idempotency fingerprints by `migrations/0004_journal_idempotency_fingerprint.sql`. Apply them in numeric order to a new development database.

## Safety and execution status
- This migration set is a design baseline for a NEW development database.
- It has NOT been applied to Floot or to any production database.
- All four migrations are executed in CI against a fresh, ephemeral PostgreSQL 18 service database. This validates schema creation but is not a backup/restore rehearsal or a production deployment.
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
- Draft creation is NOT approval or posting. Cross-row balancing at the database layer, approval, posting atomicity, immutable posted records, period locking, and audit completeness remain required controls before production.
- Every tenant-scoped query must use the authenticated tenant context; never accept tenant identity from an untrusted request body as authorization.
- Company-scoped API access additionally requires an active `company_memberships` row for the authenticated user, tenant, and company. Membership provisioning is administrative; `role_code` is reserved for upcoming fine-grained RBAC enforcement.
