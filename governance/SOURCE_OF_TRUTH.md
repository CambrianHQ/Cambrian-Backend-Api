# Source of Truth (SoT)

> This document defines the authoritative sources for all system behavior.
> All engineers, agents, and automation MUST follow this hierarchy.
> If any conflict exists, follow the highest priority source.
> Last updated: 2026-03-19

---

## Priority Order (Highest to Lowest)

1. `contracts/API_CONTRACTS.md`
2. `config/.env.example`
3. `policy/POLICY.md`
4. `architecture/ARCHITECTURE.md`
5. Manifests (`manifests/FRONTEND_MANIFEST.json` + `manifests/BACKEND_MANIFEST.json`)
6. Application Code (Frontend / Backend)

---

## 1. API_CONTRACTS.md — PRIMARY SOURCE OF TRUTH

**Location:** `contracts/API_CONTRACTS.md`

Defines:
- All API endpoints
- Request shapes
- Response shapes

Rules:
- Frontend MUST only use fields defined here
- Backend MUST return exactly what is defined here
- No field may be added, removed, or renamed without updating this file

---

## 2. .env.example — CONFIG SOURCE OF TRUTH

**Location:** `config/.env.example`

Defines:
- All required environment variables
- Naming conventions for configuration

Rules:
- No environment variable may be used unless defined here
- All environments (dev, staging, prod) must conform to this structure
- Missing variables must fail fast

---

## 3. POLICY.md — RULE SOURCE OF TRUTH

**Location:** `policy/POLICY.md`

Defines:
- Engineering rules
- Data handling constraints
- Safety requirements

Key Rules:
- No destructive database operations
- Email is internal-only
- Username is public identity
- No hardcoded configuration
- All changes must be backward compatible

---

## 4. ARCHITECTURE.md — SYSTEM DESIGN SOURCE OF TRUTH

**Location:** `architecture/ARCHITECTURE.md`

Defines:
- System structure
- Data flow
- Core entities

Rules:
- All features must align with defined architecture
- No hidden services or flows

---

## 5. MANIFESTS — SYSTEM VISIBILITY

### FRONTEND_MANIFEST.json

**Location:** `manifests/FRONTEND_MANIFEST.json`

Defines:
- Routes
- Components
- API usage
- Env dependencies

### BACKEND_MANIFEST.json

**Location:** `manifests/BACKEND_MANIFEST.json`

Defines:
- Endpoints
- Entities
- Services
- Env dependencies

Rules:
- All endpoints and dependencies must be declared
- No hidden endpoints or services

---

## ENFORCEMENT RULES

All changes MUST:

- Update relevant source-of-truth files
- Maintain alignment across:
  - database
  - backend
  - frontend
  - configuration

---

## STOP CONDITIONS

STOP immediately if:

- A field is used that is not in `contracts/API_CONTRACTS.md`
- An env variable is missing from `config/.env.example`
- A change could break payments, users, or data integrity
- Schema is unclear or mismatched

---

## DRIFT PREVENTION

Drift is defined as any mismatch between:

- database schema vs backend models
- backend responses vs API contracts
- frontend expectations vs API contracts
- env variables vs `config/.env.example`

All drift MUST be:
- detected
- documented
- resolved before deployment

---

## VALIDATION REQUIREMENTS

Before any deploy:

- Contracts match implementation
- Env variables match `config/.env.example`
- Payments flow works
- Audio player works
- Core user flows are validated

---

## FINAL RULE

If code, AI output, or assumptions conflict with Source of Truth:

**Source of Truth ALWAYS wins.**
**Code must be corrected, NOT the other way around.**
