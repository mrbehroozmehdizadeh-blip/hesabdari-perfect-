# Identity and authorization baseline

## Implemented in the current API

- JWT authentication is enabled only when `Authentication:SigningKey` is configured.
- When enabled, `Authentication:Issuer` and `Authentication:Audience` are mandatory.
- Two mutually exclusive JWT validation profiles are supported: a local HS256 profile using a private shared key, and an OIDC profile using HTTPS discovery/JWKS with RS256 or ES256 signatures. Both require expiration, validate issuer, audience, signature and lifetime, and allow 30 seconds of clock skew.
- The signing key must contain at least 32 UTF-8 bytes.
- The API preserves JWT claim names and requires both `tenant_id` and `sub` to be non-empty GUIDs. Invalid or missing identity claims fail authentication before a financial route runs.
- Tenant identity comes from the validated token, never from a request body or query parameter.
- Every protected financial endpoint evaluates an active company membership and an explicit permission from the database role catalog. Role claims supplied by a client token are not used to grant finance permissions.
- Permission evaluation joins the active membership, active company role and `role_permissions` grant. Missing roles or grants deny access. Migration 0014 adds a validated database foreign key so every stored company membership must reference an existing catalog role; role renames/deletions are restricted while referenced.
- Approval and posting are separate permissions. The accountant role can prepare and submit a journal but cannot approve or post it. The approver cannot post; the poster cannot approve.
- The seeded `FINANCE_ADMIN` role has every permission in the current catalog, but no end-user role assignment or user-administration endpoint is exposed yet.

## Membership governance baseline\n\nMigrations 0015–0019 add a tenant-scoped request queue and guarded API routes under `/api/companies/{companyId}/membership-changes`: `GET /` lists requests, `POST /` requests a role change, and `POST /{requestId}/approve` or `/reject` records an independent decision. The requester cannot target their own identity; neither the requester nor the target user can review the request; `FINANCE_ADMIN` is excluded from ordinary role requests; and only one pending request per target user/company is allowed. Rejected requests require a non-empty reviewer rationale, stored separately from the original request reason. Approval updates the target company membership in the same transaction as the decision and audit event. The `FINANCE_MEMBERSHIP_MANAGER` role can read and request changes; `FINANCE_MEMBERSHIP_REVIEWER` can read and review them. `FINANCE_ADMIN` receives all three membership permissions. Row-level security is enabled on the request table. Role-change requests are now limited to an already-existing, active membership in the same company; both the API and a database trigger enforce this, and approval updates the existing membership rather than creating or reactivating one. The request path locks the target membership row while validating it and rejects a request that repeats the user’s current role, avoiding stale membership checks and no-op approval records. New-user invitations, identity existence checks against an external directory and account lifecycle are deliberately unavailable until a trusted user directory is integrated. The membership queue and API are a controlled development baseline, not production-ready provisioning.

### Membership change API contract

| Method and route | Permission | Behavior |
|---|---|---|
| `GET /api/companies/{companyId}/membership-changes/` | `MEMBERSHIPS_READ` | Returns up to 500 newest requests, including requester and reviewer rationale |
| `POST /api/companies/{companyId}/membership-changes/` | `MEMBERSHIPS_REQUEST` | Body: `targetUserId`, `requestedRoleCode`, `reason`; creates a PENDING role-change request for an existing active company member |
| `POST /api/companies/{companyId}/membership-changes/{requestId}/approve` | `MEMBERSHIPS_REVIEW` | Approves and applies the role change atomically |
| `POST /api/companies/{companyId}/membership-changes/{requestId}/reject` | `MEMBERSHIPS_REVIEW` | Body: `reason`; rejects with mandatory reviewer rationale |

All routes require a validated bearer identity and an active company membership with the named permission. The API derives tenant and actor from validated claims, scopes queries by tenant and company, and writes request/decision audit events in the same database transaction. A role request cannot grant `FINANCE_ADMIN`; that high-privilege role requires a separately designed break-glass provisioning process.\n\n## Current role catalog

| Role | Intended boundary |
|---|---|
| `FINANCE_VIEWER` | Read-only finance information |
| `FINANCE_ACCOUNTANT` | Accounts, dimensions and journal preparation |
| `FINANCE_APPROVER` | Independent journal approval |
| `FINANCE_POSTER` | Posting approved journals to the ledger |
| `FINANCE_PERIOD_CLOSER` | Close an open fiscal period |
| `FINANCE_PERIOD_LOCKER` | Lock a closed fiscal period |
| `FINANCE_OPENING_MANAGER` | Opening balances and carry-forward |
| `FINANCE_YEAR_END_MANAGER` | Year-end closing draft |
| `FINANCE_ADMIN` | All currently catalogued permissions |\n| `FINANCE_MEMBERSHIP_MANAGER` | Read memberships and request role changes |\n| `FINANCE_MEMBERSHIP_REVIEWER` | Read and independently review role-change requests |

Role catalog and grants are seeded by migration 0013. Do not assign a role based on a browser-provided value. Membership provisioning must be an authenticated administrative workflow with audit history and maker-checker controls.

## Not implemented; production blockers

- No password login, account recovery, invitation, user lifecycle or identity-provider integration is implemented.
- No token issuance, refresh-token rotation, logout/revocation list, MFA, SSO, SCIM or device/session administration is implemented.
- The local HS256 signing key is a shared secret and is for development/test only. Production must configure `Authentication:Authority` to a trusted HTTPS OIDC authority and `Authentication:Audience` to the exact API audience; the API then validates asymmetric RS256/ES256 signatures using discovery/JWKS. Issuer must be supplied by trusted discovery metadata, and provider, audience allowlists and key rotation must be reviewed.
- No role/membership administration UI, user directory, invitation or identity-provider provisioning is implemented. The request/review API and decision audit trail exist, but operational identity verification and a complete administrative lifecycle remain outstanding.
- The seeded administrator role is not assigned automatically. A fresh database has no users or company memberships provisioned by this migration.
- The current database role and RLS setup still require production hardening, least-privilege grants, FORCE RLS verification and backup/restore rehearsal.

## Authentication configuration profiles

Local Docker uses the HS256 development profile:

- `Authentication__SigningKey`: at least 32 random bytes.
- `Authentication__Issuer`: exact local issuer.
- `Authentication__Audience`: exact API audience.

For a trusted OIDC provider, configure `Authentication__Authority` as an absolute HTTPS discovery authority and `Authentication__Audience` as the API audience. Do not configure `Authentication__SigningKey` at the same time. The API reads signing keys from the provider JWKS endpoint and accepts RS256 or ES256. The provider must issue the required `tenant_id` and `sub` GUID claims, or an upstream claims transformation must add them from trusted identity data.

## Required token claims

For local HS256 development, set these secrets privately (never commit them):

- `Authentication__SigningKey`: at least 32 random UTF-8 bytes.
- `Authentication__Issuer`: exact expected token issuer.
- `Authentication__Audience`: exact expected API audience.

Local-profile tokens must be signed with HS256. OIDC-profile tokens must use RS256 or ES256. Both must contain `iss`, `aud`, `exp`, `tenant_id` and `sub`. Tenant and subject claims must parse as non-empty GUIDs.

## Security verification

The integration suite checks anonymous access denial; missing tenant or subject claims; expired tokens; incorrect issuer or audience; disallowed signing algorithms; and malformed membership requests that must be rejected before any database operation. PostgreSQL CI executes migration 0013 and the rollback-only `database/tests/rbac_invariants.sql` checks for viewer read-only behavior and separation of journal approval from posting.

Passing these checks is a development baseline, not a production security certification.

## Local chart-of-accounts development access

The Docker development stack serves the React application and proxies `/api/*` to the private ASP.NET Core API on the Docker network. The chart-of-accounts workspace calls the protected account endpoints for list, create, rename, activation/deactivation and guarded deletion. The browser keeps the supplied bearer token in component memory only; it does not persist the token in local storage.

For local development only, `scripts/dev/Initialize-LocalCompany.ps1` creates a new tenant, company and `FINANCE_ADMIN` membership, writes a bootstrap audit event, and issues an eight-hour HS256 token using the local Compose signing key. It must be run only against the local development Compose database. The generated role and token are deliberately not a production identity workflow. Production must use a trusted HTTPS OIDC provider with asymmetric signing keys and a controlled user/company provisioning process.

The static GitHub Pages preview does not have a backend and cannot use the live chart-of-accounts operations. The operational UI path is the local Docker web origin (or a separately deployed web origin configured with an approved API gateway and identity provider).
