# Identity and authorization baseline

## Implemented in the current API

- JWT authentication is enabled only when `Authentication:SigningKey` is configured.
- When enabled, `Authentication:Issuer` and `Authentication:Audience` are mandatory.
- The current prototype accepts signed HS256 tokens only, requires an expiration time, validates issuer, audience, signature and lifetime, and allows 30 seconds of clock skew.
- The signing key must contain at least 32 UTF-8 bytes.
- The API preserves JWT claim names and requires both `tenant_id` and `sub` to be non-empty GUIDs. Invalid or missing identity claims fail authentication before a financial route runs.
- Tenant identity comes from the validated token, never from a request body or query parameter.
- Every protected financial endpoint evaluates an active company membership and an explicit permission from the database role catalog. Role claims supplied by a client token are not used to grant finance permissions.
- Permission evaluation joins the active membership, active company role and `role_permissions` grant. Missing roles or grants deny access.
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
- The current HS256 signing key is a shared secret. This is a prototype trust configuration, not the preferred production identity architecture. Production should integrate with a trusted OIDC identity provider and validate asymmetric signatures/JWKS, with issuer/audience allowlists and managed key rotation.
- No role/membership administration UI or approval workflow exists yet.
- The seeded administrator role is not assigned automatically. A fresh database has no users or company memberships provisioned by this migration.
- The current database role and RLS setup still require production hardening, least-privilege grants, FORCE RLS verification and backup/restore rehearsal.

## Required configuration for local API execution

Set these secrets privately (never commit them):

- `Authentication__SigningKey`: at least 32 random UTF-8 bytes.
- `Authentication__Issuer`: exact expected token issuer.
- `Authentication__Audience`: exact expected API audience.

Tokens must be signed with HS256 and contain `iss`, `aud`, `exp`, `tenant_id` and `sub`. Tenant and subject claims must parse as non-empty GUIDs.

## Security verification

The integration suite checks anonymous access denial and rejects correctly signed tokens that omit tenant or subject identity, as well as tokens using a disallowed signing algorithm. PostgreSQL CI executes migration 0013 and the rollback-only `database/tests/rbac_invariants.sql` checks for viewer read-only behavior and separation of journal approval from posting.

Passing these checks is a development baseline, not a production security certification.
