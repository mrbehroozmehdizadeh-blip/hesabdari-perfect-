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

## Current role catalog

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
| `FINANCE_ADMIN` | All currently catalogued permissions |

Role catalog and grants are seeded by migration 0013. Do not assign a role based on a browser-provided value. Membership provisioning must be an authenticated administrative workflow with audit history and maker-checker controls.

## Not implemented; production blockers

- No password login, account recovery, invitation, user lifecycle or identity-provider integration is implemented.
- No token issuance, refresh-token rotation, logout/revocation list, MFA, SSO, SCIM or device/session administration is implemented.
- The local HS256 signing key is a shared secret and is for development/test only. Production must configure `Authentication:Authority` to a trusted HTTPS OIDC authority and `Authentication:Audience` to the exact API audience; the API then validates asymmetric RS256/ES256 signatures using discovery/JWKS. Issuer must be supplied by trusted discovery metadata, and provider, audience allowlists and key rotation must be reviewed.
- No role/membership administration UI or approval workflow exists yet. The database now enforces role-code referential integrity, but authorized provisioning, role-change approval, actor attribution and administrative audit events still require implementation.
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

The integration suite checks anonymous access denial and rejects correctly signed tokens that omit tenant or subject identity, as well as tokens using a disallowed signing algorithm. PostgreSQL CI executes migration 0013 and the rollback-only `database/tests/rbac_invariants.sql` checks for viewer read-only behavior and separation of journal approval from posting.

Passing these checks is a development baseline, not a production security certification.
