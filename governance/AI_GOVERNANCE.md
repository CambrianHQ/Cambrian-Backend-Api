# AI Governance Rules

> Binding rules for all AI agents (Copilot, ChatGPT, Claude, etc.) operating in this codebase.
> These rules are non-negotiable. If uncertain — **STOP and ask the human**.

---

## 1. Do NOT Break API Contracts

- Read `contracts/API_CONTRACTS.md` before modifying any DTO, controller, or service.
- Do NOT change request/response shapes without updating `contracts/API_CONTRACTS.md` **first**.
- Do NOT remove or rename fields — backward compatibility is mandatory (see `policy/POLICY.md` §7).

## 2. Do NOT Introduce New Fields Without Updating Contracts

- Every new field on a DTO must be added to `contracts/API_CONTRACTS.md` **before** writing code.
- New fields MUST be nullable or have defaults — never break existing consumers.
- Update the JSON examples in `contracts/API_CONTRACTS.md` to include the new field.

## 3. Do NOT Run Destructive DB Operations

- **NEVER** run `DROP TABLE`, `DROP COLUMN`, or type-narrowing `ALTER COLUMN`.
- Migrations are additive only — append new migrations, never edit existing ones.
- Schema changes must update `manifests/BACKEND_MANIFEST.json`.

## 4. Do NOT Expose Email Publicly

- Email is **internal-only** — authentication, admin, and private profile endpoints only.
- Email must NEVER appear in track responses, storefront pages, search results, or any public API.

## 5. Always Use Username/Slug for Creator Identity

- `slug` (from `CreatorProfile`) is the **only** public creator identifier.
- `DisplayName` maps to `Artist` in track responses.
- Never use `Email`, `UserId`, or `CreatorId` as a public-facing identity.

## 6. Always Update Governance Files

Any code change must update the relevant governance files:

| What Changed | Update These |
|---|---|
| API shape (DTO fields) | `contracts/API_CONTRACTS.md`, `contracts/openapi.v1.json` |
| Endpoint added/removed | `contracts/API_CONTRACTS.md`, `contracts/endpoint-manifest.v1.json`, `contracts/openapi.v1.json` |
| Database schema | `manifests/BACKEND_MANIFEST.json`, `architecture/ARCHITECTURE.md` |
| Environment variable | `config/.env.example`, `render.yaml`, `manifests/BACKEND_MANIFEST.json` |
| Service/repo added | `manifests/BACKEND_MANIFEST.json` |
| Frontend route/API usage | `manifests/FRONTEND_MANIFEST.json` |
| Policy rule changed | `contracts/policy.v1.json`, `governance/backend-policy.v1.json` |

## 7. Required Reading Before Making Changes

Before writing any code, AI agents **must** read:

1. `policy/POLICY.md` — full engineering rules
2. `contracts/API_CONTRACTS.md` — all endpoint shapes
3. `architecture/ARCHITECTURE.md` — system design and entity relationships
4. The specific file(s) being modified — understand existing code before changing it

## 8. If Uncertain — STOP and Ask

- If a change might break an existing consumer → **ask**.
- If a migration might lose data → **ask**.
- If you're unsure whether a field is public or private → **ask**.
- If the governance update scope is unclear → **ask**.
- **Never guess. Never assume. Ask the human.**
