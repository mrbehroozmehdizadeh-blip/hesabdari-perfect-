# Database foundation

The initial schema is in `migrations/0001_financial_core.sql`.

## Safety and execution status
- This migration is a design baseline for a NEW development database.
- It has NOT been applied to Floot or to any production database.
- It has NOT yet been executed against a live PostgreSQL instance in this repository's CI.
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
- Account tree depth is capped at ten levels.
- Fixed and floating details are modeled separately; floating dimensions are not additional account-tree levels.
- Monetary values use PostgreSQL numeric(28,8), never binary floating point.
- A single journal entry uses one currency in this initial schema. Explicit FX conversion documents and multi-currency posting will be designed as a separate capability.
- Cross-row journal balancing, approval, posting atomicity, immutable posted records, period locking, and audit completeness must be enforced in the application transaction and strengthened with database constraints/triggers before production.
- Every tenant-scoped query must use the authenticated tenant context; never accept tenant identity from an untrusted request body as authorization.
