# Account API security and deployment notes

## Implemented surface

The chart-of-accounts API is available under:

- `GET /api/companies/{companyId}/accounts`
- `POST /api/companies/{companyId}/accounts`
- `PUT /api/companies/{companyId}/accounts/{accountId}` — currently changes name and active status only
- `DELETE /api/companies/{companyId}/accounts/{accountId}` — only when no child accounts and no journal-line history exist

All endpoints require a JWT bearer token. Routes are not registered unless an authentication signing key is configured; the API therefore fails closed for account operations when authentication is not configured.

## Required configuration

Set these values through deployment secrets/environment variables, never commit real secrets:

- `ConnectionStrings__Postgres`: PostgreSQL connection string.
- `Authentication__SigningKey`: random secret with at least 32 UTF-8 bytes. Use a secret manager; do not reuse a human password.
- `Authentication__Issuer`: exact trusted token issuer.
- `Authentication__Audience`: exact API audience.
- `Cors__AllowedOrigins__0`: exact browser origin, e.g. `https://finance.example.org`. Add one indexed value per explicitly trusted origin.

The current API validates issuer, audience, signing key, token expiry, and the JWT signature. The token must contain a UUID `tenant_id` claim and a UUID `sub` (or NameIdentifier) claim. Tenant identity is read only from validated claims, never from request JSON.

A trusted identity provider must issue these tokens. This project does not yet implement user registration, password login, refresh-token rotation, MFA, role/permission administration, or key rotation. Do not create production tokens manually or expose the signing key to the browser.

## Tenant isolation and database role

Each account request opens a database transaction and sets `app.tenant_id` with transaction-local `set_config(..., true)`. PostgreSQL row-level security policies provide defense in depth.

Before production:
1. Use a dedicated database application role that is not a table owner and has no `BYPASSRLS` privilege.
2. Consider `FORCE ROW LEVEL SECURITY` after validating migration and operational behavior.
3. Restrict schema/table privileges to the minimum required.
4. Require TLS for API and database connections, secret rotation, backups, restore drills, rate limits, structured security logs, and monitoring.
5. Add explicit authorization policies for company membership and role-based access. Tenant claim validation alone does not prove that a user is authorized for every company in that tenant.

## Deletion policy and limitations

Deletion is blocked when an account has child accounts or any journal-line history. This is intentionally conservative and includes draft journal lines. Accounts should normally be deactivated instead.

There is not yet a balance table or a posted-ledger engine. Before enabling accounting production use, add authoritative balance/ledger controls, immutable posting, approval workflow, period locks, concurrency control, and integration tests. The current account endpoints are an initial module, not a production-ready accounting system.

## Browser client

The React screen accepts API URL, company UUID, and a short-lived JWT in memory only. It does not persist the token in local storage. The browser origin must be explicitly allowed in `Cors:AllowedOrigins`. Never place a signing key or permanent credential in Vite environment variables or frontend source.
