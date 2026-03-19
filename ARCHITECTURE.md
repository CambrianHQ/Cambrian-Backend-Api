# Cambrian Architecture

## Overview

Cambrian is a music marketplace API built with ASP.NET Core 8, PostgreSQL, and clean architecture.

## Layers

```
Cambrian.Api            → Controllers, middleware, DI wiring
Cambrian.Application    → DTOs, services, interfaces (business logic)
Cambrian.Domain         → Entities, enums (no dependencies)
Cambrian.Infrastructure → External services (S3, Stripe, Email)
Cambrian.Persistence    → EF Core DbContext, repositories, migrations
```

### Dependency Flow

```
Api → Application → Domain
Api → Infrastructure → Application
Api → Persistence → Application, Domain
```

Controllers MUST NOT access `DbContext` directly. All data access goes through repository interfaces defined in `Application` and implemented in `Persistence`.

## Identity Model

### Public Identity

- **Public creator identity = DisplayName** (never email).
- `ApplicationUser.DisplayName` is the public-facing name shown on song cards, profiles, and storefronts.
- If `DisplayName` is null/empty, the system falls back to the email-prefix (the portion before `@`), never the full email.
- `CreatorProfile.Slug` is the URL-safe identifier for creator profile pages.

### Internal Identity

- `ApplicationUser.Email` is used exclusively for authentication, account recovery, and internal system operations.
- Email MUST NOT appear in any public API response field (`Artist`, `creatorUsername`, etc.).
- Email MAY appear in authenticated-only responses scoped to the user's own account (e.g., `/auth/me`, `/settings/profile`).

## Duplicate Upload Prevention

- Each track upload computes a SHA-256 hash of the audio file content.
- The hash is stored in `Track.AudioFileHash`.
- Uniqueness is enforced per-creator: `(CreatorId, AudioFileHash)`.
- Different creators MAY upload identical audio files (they own their own copies).
- The same creator uploading the same file twice is rejected with a `409 Conflict`.
- Existing tracks uploaded before hash enforcement have `AudioFileHash = NULL` and are never considered duplicates.

## Creator Profile

- Creators have a `CreatorProfile` entity linked via `UserId`.
- The profile includes slug, bio, niche, social links, banner image, profile image.
- Public profile pages are accessed via `GET /creator-profile/{slug}`.
- Full storefront (profile + stats + tracks + collections) via `GET /creator-profile/{slug}/storefront`.
- Profile images: JPEG, PNG, WebP; max 10 MB; stored via `IObjectStorage`.
- Users without a profile image render a default avatar on the frontend.

## Track Response Contract

Every public track response includes:
- `creatorId` — internal user ID
- `artist` — display name (never email)
- `creatorUsername` — display name for linking
- `creatorSlug` — URL slug for creator profile navigation (null if no profile)
- `creatorProfileImageUrl` — avatar URL (null if not set)

## API Envelope

All responses use a standard envelope:
```json
{
  "success": true|false,
  "data": <payload>,
  "message": "optional message",
  "error": "optional error"
}
```

## Storage

- Audio files: S3/R2/local, keyed under `tracks/{creatorId}/{guid}.{ext}`
- Cover art: `covers/{creatorId}/{guid}.{ext}`
- Profile images: `avatars/{guid}.{ext}`, `banners/{guid}.{ext}`

## Authentication

- JWT Bearer tokens with 24-hour expiry.
- Claims: `sub` (user ID), `email`, `role`, `tier`.
- ASP.NET Core Identity for user management.
