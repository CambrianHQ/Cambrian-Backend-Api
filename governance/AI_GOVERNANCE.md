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


- If the governance update scope is unclear → **ask**.
- **Never guess. Never assume. Ask the human.**
