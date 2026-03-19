# Cambrian Policy

## Public Creator Identity

**Rule**: Public creator identity = `DisplayName`, never email.

- All public-facing API fields that represent a creator's name MUST use `ApplicationUser.DisplayName`.
- If `DisplayName` is null or empty, use the portion of the email before `@` as a fallback.
- The full email address MUST NOT appear in any public API response.
- Email is permitted only in authenticated, self-scoped responses (`/auth/me`, `/settings/profile`).

## Duplicate Upload Prevention

**Rule**: A creator cannot upload the same audio file twice.

- **Detection method**: SHA-256 hash of the raw audio file bytes.
- **Scope**: Per-creator. The uniqueness constraint is `(CreatorId, AudioFileHash)`.
- **Cross-creator**: Different creators MAY upload identical files. This is intentional — they may independently own rights to the same content.
- **Legacy tracks**: Tracks uploaded before hash enforcement have `AudioFileHash = NULL`. These are never flagged as duplicates.
- **Error**: Duplicate uploads return HTTP 409 Conflict with a clear message identifying the existing track.
- **Future hardening**: A database UNIQUE constraint on `(CreatorId, AudioFileHash) WHERE AudioFileHash IS NOT NULL` may be added once the backfill of existing tracks is complete.

## Profile Image Rules

- **Accepted types**: JPEG (.jpg, .jpeg), PNG (.png), WebP (.webp)
- **Max size**: 10 MB
- **Storage**: Via `IObjectStorage` under `avatars/` prefix.
- **Default behavior**: Users without a profile image return `profileImageUrl = null`. The frontend MUST render a default avatar placeholder.
- **MIME validation**: Content type must match `image/jpeg`, `image/png`, or `image/webp`.

## Data Safety

- **No destructive migrations**: Never drop columns, rename columns, or delete data in production migrations.
- **Additive only**: New columns must be nullable or have safe defaults.
- **Backward compatibility**: New response fields are additive. Existing clients that ignore unknown fields continue to work.
- **Email preservation**: Email remains the internal identifier for auth. Never overwrite or remove email data.

## API Contract Enforcement

- All endpoints must exist in `contracts/openapi.v1.json`.
- Response shapes must match DTOs — no dynamic anonymous objects in new endpoints.
- The `contracts/endpoint-manifest.v1.json` must be updated when endpoints change.
- The `scripts/validate-contracts.cjs` CI step enforces contract compliance.
