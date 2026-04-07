# Creator Profile Audit Report

**Date:** 2025-07-24
**Scope:** Creator profile setup + management — every code path from "becoming a creator" through public storefront rendering
**Type:** Read-only audit. No fixes applied.

---

## Findings

### F1 — `PUT /creator-profile/me` allows any authenticated user to create a profile

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **File:Line** | [CreatorProfileController.cs](src/Cambrian.Api/Controllers/CreatorProfileController.cs#L131) |
| **Why** | `PUT /creator-profile/me` is decorated with `[Authorize]` only — no `[RequireCreatorTier]`, no `[RequireUsername]`. A user with Role=`User` who has never set a username can create a CreatorProfile and slug. |
| **Failure mode** | A non-creator user can claim a slug that a real creator would want. The slug uniqueness constraint means the real creator is blocked. Non-creators populate the CreatorProfiles table with data never visible through the creator flow. |
| **Fix** | Add `[RequireCreatorTier]` and `[RequireUsername]` to the `UpsertProfile` action. |
| **Tests** | 1) Register a User-role account, call `PUT /creator-profile/me` with a slug → expect 403. 2) Set username, promote to Creator, retry → expect 200. |

---

### F2 — `PATCH /creator-profile/me/settings` allows non-creators to modify settings

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **File:Line** | [CreatorProfileController.cs](src/Cambrian.Api/Controllers/CreatorProfileController.cs#L186) |
| **Why** | Same as F1 — only `[Authorize]`, no tier/username gates. A non-creator can PATCH `showEarnings`/`showDownloadStats` if a profile exists (e.g., auto-created by image upload via F3). |
| **Failure mode** | Low impact since settings only affect an already-existing profile, but it breaks the expected access control invariant. |
| **Fix** | Add `[RequireCreatorTier]`. |
| **Tests** | Call `PATCH /creator-profile/me/settings` as User-role → expect 403. |

---

### F3 — Avatar/banner upload auto-creates placeholder profile without Creator role check

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **File:Line** | [CreatorProfileController.cs](src/Cambrian.Api/Controllers/CreatorProfileController.cs#L211) (UploadAvatar) and [CreatorProfileController.cs](src/Cambrian.Api/Controllers/CreatorProfileController.cs#L196) (UploadBanner) and [CreatorProfileRepository.cs](src/Cambrian.Persistence/Repositories/CreatorProfileRepository.cs#L54-L65) (`UpdateImageAsync` auto-create) |
| **Why** | `POST /creator-profile/me/avatar` and `POST /creator-profile/me/banner` require only `[Authorize]`. `UpdateImageAsync` auto-creates a placeholder CreatorProfile with `Slug = userId[..16]`. This lets any authenticated user inject a CreatorProfile row with a truncated-userId slug. |
| **Failure mode** | The auto-created placeholder slug (e.g., `ab12cd34ef56gh78`) occupies a unique index slot. If a future creator happens to want that slug, it's taken. More importantly, an attacker could mass-create profiles as a DoS against the CreatorProfiles table. |
| **Fix** | Add `[RequireCreatorTier]` to both image upload endpoints. Or remove the auto-create behavior from `UpdateImageAsync` and return 404 if no profile exists. |
| **Tests** | 1) As User-role, call `POST /creator-profile/me/avatar` → expect 403 (currently 200). 2) Verify no CreatorProfile row is created. |

---

### F4 — `POST /uploads/image` (generic) has no magic-byte validation

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **File:Line** | [UploadController.cs](src/Cambrian.Api/Controllers/UploadController.cs#L83-L112) |
| **Why** | `CreatorProfileController.UploadImage` validates magic bytes (JPEG `FF D8 FF`, PNG `89 50 4E 47`, WEBP `RIFF`), but `UploadController.UploadImage` only checks file extension. A `.jpg` file containing HTML/JS could be uploaded. |
| **Failure mode** | Stored XSS if the file is served with permissive content-type headers. Lower risk because `contentType` is set from extension (not from user-supplied content-type), so S3/R2 would serve it as `image/jpeg`. |
| **Fix** | Add the same magic-byte validation from `CreatorProfileController` to `UploadController.UploadImage`. |
| **Tests** | Upload a file named `evil.jpg` with HTML content → expect rejection. |

---

### F5 — Proxy upload endpoint does not verify ownership of the presigned key

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **File:Line** | [CreatorsController.cs](src/Cambrian.Api/Controllers/CreatorsController.cs#L314-L328) (`ProxyCreatorImageUpload`) |
| **Why** | `POST /api/uploads/creator-image-url` generates a key like `creator-profiles/{newGuid}.jpg`, but does not bind it to the requesting user. `PUT /api/uploads/creator-image/{key}` only validates prefix — any authenticated user can write to any key under `creator-profiles/` or `creator-covers/`. |
| **Failure mode** | User A requests an upload URL, gets a key. User B (or A with a different key) can PUT any content to `creator-profiles/{anyGuid}.ext`. Since S3 keys are random UUIDs, the practical risk of overwriting is very low, but a user can upload arbitrary images that are orphaned (no profile references them). |
| **Fix** | Bind the key to the requesting user (e.g., include userId in the key prefix: `creator-profiles/{userId}/{guid}.ext`) and verify the userId segment matches the authenticated user on PUT. |
| **Tests** | As User A, take a key generated for User B's session, attempt PUT → expect 403. |

---

### F6 — Dual profile tables (Creator + CreatorProfile) with unsynchronized writes

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **File:Line** | [CreatorsController.cs](src/Cambrian.Api/Controllers/CreatorsController.cs#L271-L276) (`UpdateMyProfile` — sync to CreatorProfile) and [CreatorProfileController.cs](src/Cambrian.Api/Controllers/CreatorProfileController.cs#L204) (UploadBanner — sync to Creator) |
| **Why** | Two tables (`Creator` and `CreatorProfile`) both store `ProfileImageUrl` and `CoverImageUrl`/`BannerImageUrl`. Syncing happens at the controller level after the primary write — if the sync call fails (exception, timeout), the tables drift. Neither sync is inside a transaction. |
| **Failure mode** | Creator uploads a new avatar via `PUT /api/creator/me`. The Creator table is updated. The subsequent `_profiles.UpdateImageAsync()` call throws (e.g., DB timeout). The Creator table shows the new image; the CreatorProfile table (used by storefront) shows the old one. The creator sees different images depending on which endpoint they check. |
| **Fix** | Either (a) consolidate into one table, or (b) wrap both writes in a DB transaction, or (c) make one table the source of truth and have read paths fall back (the `MapToDtoAsync` already falls back: `p.ProfileImageUrl ?? creator?.ProfileImageUrl`). |
| **Tests** | Mock `_profiles.UpdateImageAsync` to throw after `_creators.UpsertAsync` succeeds. Verify behavior. |

---

### F7 — Slug and Username have different constraints but overlap in routing

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **File:Line** | [CreatorProfileController.cs](src/Cambrian.Api/Controllers/CreatorProfileController.cs#L139-L140) (slug: 3-100 chars) vs [AuthController.cs](src/Cambrian.Api/Controllers/AuthController.cs#L246) (username: 3-40 chars, `^[a-z0-9_-]+$`) |
| **Why** | Slug allows 3-100 characters with no character class validation. Username allows 3-40 characters, alphanumeric + hyphens + underscores. The storefront `GET /{slug}` endpoint falls back to `Creator.Username` lookup when slug is not found. A creator with slug `my slug!` (spaces, special chars) gets a valid profile, but cannot be found via the username fallback. |
| **Failure mode** | A creator can set a slug containing characters that are not URL-safe (spaces, unicode, special chars), leading to broken links. Since slug has no regex validation, it can contain SQL-like or script-like strings — though these are parameterized, it's a defense-in-depth gap. |
| **Fix** | Add a regex constraint to slug matching the username rules (`^[a-z0-9][a-z0-9_-]*[a-z0-9]$`) and reduce max length to 40 to match. Or at minimum validate slug is URL-safe. |
| **Tests** | 1) `PUT /creator-profile/me` with slug `my slug!` → expect rejection. 2) Slug `a` (1 char) → expect rejection. 3) Slug with 101 chars → expect rejection. |

---

### F8 — Self-follow is not prevented

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **File:Line** | [CreatorsController.cs](src/Cambrian.Api/Controllers/CreatorsController.cs#L387-L397) (Follow by UUID) and [CreatorProfileController.cs](src/Cambrian.Api/Controllers/CreatorProfileController.cs#L341-L349) (Follow by slug) |
| **Why** | Neither follow endpoint checks if `userId == creator.UserId`. A creator can follow themselves, inflating their follower count. |
| **Failure mode** | Vanity metric inflation. Follower count shown on storefront includes self-follows. Low severity since it's only cosmetic. |
| **Fix** | Add `if (userId == creator.UserId) return ErrorResponse("Cannot follow yourself.")` before calling `FollowAsync`. |
| **Tests** | As a creator, follow your own profile → expect 400. |

---

### F9 — `UpdatePinnedTracks` does not verify track ownership

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **File:Line** | [CreatorProfileController.cs](src/Cambrian.Api/Controllers/CreatorProfileController.cs#L330-L339) |
| **Why** | `PUT /creator-profile/me/pinned-tracks` saves `body.TrackIds` directly without calling `AllTracksOwnedByCreatorAsync`. Collections CRUD does validate ownership (line 272, 294), but pinned tracks skips it. |
| **Failure mode** | A creator can pin another creator's track IDs to their storefront. The storefront's `ResolvePinnedTracks` only shows tracks that are in the creator's track list, so the foreign track IDs would be silently filtered out. Practical impact is low (silent no-op), but it's an inconsistency of enforcement. |
| **Fix** | Add `if (!await AllTracksOwnedByCreatorAsync(body.TrackIds, userId)) return ErrorResponse("One or more tracks do not belong to you.");` before saving. |
| **Tests** | Pin a track ID belonging to another creator → expect 400. |

---

### F10 — `CreatorProfileRepository.GetBySlugAsync` uses LINQ `.ToLower()` for case-insensitive search — fragile

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **File:Line** | [CreatorProfileRepository.cs](src/Cambrian.Persistence/Repositories/CreatorProfileRepository.cs#L27) |
| **Why** | `x.Slug.ToLower() == slug.ToLower()` generates a PostgreSQL `lower()` call, which works but prevents usage of the unique index on `Slug` (the index is on the raw value, not `lower(Slug)`). The `UpsertAsync` stores slug as-is (after `ToLowerInvariant()` at the controller), but `GetBySlugAsync` does a runtime lowercase comparison. |
| **Failure mode** | If a slug is ever stored with mixed case (e.g., via a future code path that skips the controller normalization), two slugs differing only in case could coexist and cause confusion. Performance-wise, the `lower()` call prevents index-only scans for slug lookups. |
| **Fix** | Store slug normalized (already done at controller level). Change query to `x.Slug == slug.ToLowerInvariant()` (exact match on pre-normalized data). Or add a unique index on `lower("Slug")`. |
| **Tests** | Store slug as `testcreator`. Query with `TestCreator` → must find it. |

---

### F11 — Admin `SetUserRole` can promote to Creator without creating a Creator row or username

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **File:Line** | [AdminController.cs](src/Cambrian.Api/Controllers/AdminController.cs#L143-L153) |
| **Why** | `POST /admin/users/{id}/role` with `{"role":"Creator"}` sets `ApplicationUser.Role = "Creator"` via `AdminService`, but does not create a Creator table row or set a username. The user now passes `[RequireCreatorTier]` but fails `[RequireUsername]` — they're in a half-onboarded state that only `POST /auth/set-username` resolves. |
| **Failure mode** | Admin promotes a user to Creator. User can now access `GET /api/creator/me` (only requires `[RequireCreatorTier]`) but gets 404 because no Creator row exists. User cannot upload tracks (requires `[RequireUsername]`). Confusing UX. |
| **Fix** | Option A: When admin sets role to Creator, also create a Creator row (requires username). Option B: Document that admin must also ensure username is set. Option C: Have `RequireCreatorTier` also check Creator row existence. |
| **Tests** | Admin promotes user to Creator who has no username. User calls `GET /api/creator/me` → expect 404. User calls `POST /upload` → expect 403 (USERNAME_REQUIRED). |

---

### F12 — `StorefrontService` uses `CreatorId` (legacy string) for track queries, not `CreatorUuid`

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **File:Line** | [StorefrontService.cs](src/Cambrian.Application/Services/StorefrontService.cs#L47) → [TrackRepository.cs](src/Cambrian.Persistence/Repositories/TrackRepository.cs#L99-L110) (`GetStorefrontTracksAsync`) |
| **Why** | `StorefrontService.GetStorefrontAsync` calls `_tracks.GetStorefrontTracksAsync(profile.UserId)` which filters by `t.CreatorId == creatorId` (the legacy `ApplicationUser.Id` string FK). Meanwhile, `CreatorIdentityRepository.GetTracksByCreatorIdAsync` filters by `t.CreatorUuid == creatorId` (the UUID FK). New tracks uploaded via the UUID-based flow only set `CreatorUuid`. Tracks uploaded via the legacy flow only set `CreatorId`. |
| **Failure mode** | A creator's storefront shows tracks uploaded via the legacy path. Tracks uploaded after the UUID migration that only have `CreatorUuid` set are invisible on the storefront. Or vice versa if the upload path only sets the legacy `CreatorId`. |
| **Fix** | Update `GetStorefrontTracksAsync` to filter by both `CreatorId == userId OR CreatorUuid == creatorUuid` (OR logic), or ensure the upload path always sets both FKs. |
| **Tests** | Upload a track via the current flow. Verify it appears on both the storefront (slug route) and the UUID track listing. |

---

### F13 — `CreatorProfileRepository.ComputeStatsAsync` uses legacy `CreatorId` for earnings

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **File:Line** | [CreatorProfileRepository.cs](src/Cambrian.Persistence/Repositories/CreatorProfileRepository.cs#L145-L155) |
| **Why** | `ComputeStatsAsync` counts purchases via `t.CreatorId == userId` and wallet transactions via `w.UserId == userId`. `CreatorIdentityRepository.GetStatsAsync` counts via `t.CreatorUuid == creatorId`. These two stats computations may return different numbers during the UUID migration period. |
| **Failure mode** | The storefront shows different stats than the creator dashboard. TotalDownloads/TotalEarnings may be undercounted or overcounted depending on which FK is populated. |
| **Fix** | Standardize on one stats computation method. Either both use UUID or both use legacy, or unify the query. |
| **Tests** | Create tracks with legacy FK only and UUID FK only. Compare stats between storefront and creator dashboard. |

---

### F14 — `UploadController.Upload` uses `FindFirstValue` instead of `GetRequiredUserId`

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **File:Line** | [UploadController.cs](src/Cambrian.Api/Controllers/UploadController.cs#L47) |
| **Why** | `User.FindFirstValue(ClaimTypes.NameIdentifier)` returns `null` if the claim is missing (edge case: corrupted JWT). Every other controller uses `GetRequiredUserId()` which throws a clear error. Here, `null` is passed to `request.CreatorId`, eventually causing an unclear error deeper in the stack. |
| **Failure mode** | Null userId propagates, causing a confusing DB error instead of a clean 401/403. |
| **Fix** | Replace with `GetRequiredUserId()`. |
| **Tests** | Call `/upload` with a JWT missing the NameIdentifier claim → expect clean 401 (not 500). |

---

### F15 — `AllTracksOwnedByCreatorAsync` uses legacy `CreatorId` for ownership

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **File:Line** | [CreatorProfileController.cs](src/Cambrian.Api/Controllers/CreatorProfileController.cs#L48-L58) |
| **Why** | Ownership check: `track.CreatorId == userId`. If a track was uploaded after the UUID migration and only has `CreatorUuid` set (no legacy `CreatorId`), the ownership check fails — the creator cannot add their own track to a collection. |
| **Failure mode** | Creator creates a track (UUID path), then tries to add it to a collection → rejected with "does not belong to you." |
| **Fix** | Also check `track.CreatorUuid` matches the creator's UUID (look up via `_creators.GetCreatorIdForUserAsync(userId)`). |
| **Tests** | Upload track via UUID path only. Add to collection → expect success. |

---

### F16 — Storefront earnings are recomputed on every request (no caching)

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **File:Line** | [StorefrontService.cs](src/Cambrian.Application/Services/StorefrontService.cs#L45-L49) and [CreatorProfileRepository.cs](src/Cambrian.Persistence/Repositories/CreatorProfileRepository.cs#L145-L155) |
| **Why** | Every `GET /{slug}/storefront` call runs two aggregate queries (COUNT purchases, SUM wallet transactions). For popular creators with many transactions, this is a N+1-style perf concern on every page load. |
| **Failure mode** | Slow storefront loads under traffic. No correctness issue — purely performance. |
| **Fix** | Cache stats with a short TTL (30-60 seconds) using `IMemoryCache`. |
| **Tests** | Hit storefront endpoint twice in 10 seconds → verify second call uses cache (lower DB query count). |

---

### F17 — `CreatorStatsResponseDto.FollowerCount` is always 0

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **File:Line** | [CreatorIdentityRepository.cs](src/Cambrian.Persistence/Repositories/CreatorIdentityRepository.cs#L125-L135) (`GetStatsAsync`) |
| **Why** | `GetStatsAsync` initializes `FollowerCount = 0` but never queries the CreatorFollows table. The `GetFollowerCountAsync` method exists (line 185) but is not called from `GetStatsAsync`. |
| **Failure mode** | Creator profile via UUID path always shows 0 followers regardless of actual count. Storefront stats (`CreatorProfileRepository.ComputeStatsAsync`) doesn't have a follower count field at all. |
| **Fix** | Call `GetFollowerCountAsync(creatorId)` inside `GetStatsAsync` and assign to `FollowerCount`. |
| **Tests** | Follow a creator. GET their profile → verify followerCount > 0. |

---

### F18 — `GET /creator-profile/{slug}/collections` has no visibility/status check on the profile owner

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **File:Line** | [CreatorProfileController.cs](src/Cambrian.Api/Controllers/CreatorProfileController.cs#L247-L253) |
| **Why** | Public collection listing resolves profile by slug, then passes `profile.UserId` to `GetCollectionsAsync`. No check that the associated ApplicationUser is active (not suspended). A suspended creator's collections remain publicly visible. |
| **Failure mode** | Suspended creators' content remains accessible. This may be intentional (content vs. account separation), but it's worth flagging as a policy decision. |
| **Fix** | Check `ApplicationUser.Status == "active"` before returning collections. Or add a `Suspended` check at the profile/storefront level. |
| **Tests** | Suspend a creator. `GET /{slug}/collections` → decide policy: 404 or collections returned. |

---

### F19 — `POST /auth/set-username` upsert to Creator table is not transactional with the Identity update

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **File:Line** | [AuthController.cs](src/Cambrian.Api/Controllers/AuthController.cs#L265-L292) |
| **Why** | `SetUsername` first updates `ApplicationUser` (username, role) via UserManager, then calls `_creators.UpsertAsync(...)`. If the UpsertAsync fails (non-unique-constraint error, DB timeout), the user already has Role=Creator and username set in AspNetUsers, but no Creator row exists. The `catch(Exception ex)` at line 291 logs it as "Non-critical" and proceeds. |
| **Failure mode** | User has Role=Creator and a username but no Creator table row. They pass `[RequireCreatorTier]` and `[RequireUsername]` but any code that looks up the Creator table (e.g., `GET /api/creator/me`) returns 404. They cannot re-run `set-username` because `UsernameHelper.IsSet` returns true. Stuck state. |
| **Fix** | Wrap both operations in a transaction. If Creator upsert fails with a non-constraint error, roll back the Identity update. Or make the Creator row creation retry-safe (idempotent on later calls). |
| **Tests** | Mock `_creators.UpsertAsync` to throw a generic exception (not DbUpdateException). Verify user's Role is not promoted and username is not set. |

---

### F20 — `CreatorController.EditTrack` ownership check uses legacy `CreatorId` only

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **File:Line** | [CreatorController.cs](src/Cambrian.Api/Controllers/CreatorController.cs#L57) |
| **Why** | `track.CreatorId != userId` only checks the legacy string FK. If a track was uploaded via the UUID path and `CreatorId` is null/empty but `CreatorUuid` is set, the ownership check fails and the creator cannot edit their own track. |
| **Failure mode** | Creator uploads a track (UUID migration path), then cannot edit it via `PUT /creator/tracks/{id}`. |
| **Fix** | Also check `track.CreatorUuid` against the creator's UUID. |
| **Tests** | Upload track via UUID-only path. Attempt `PUT /creator/tracks/{id}` → expect 200 (currently 403). |

---

## Summary Sections

### A — Feature Map (what exists today)

| Feature | Route(s) | Controller | Auth Level |
|---------|----------|------------|------------|
| **Become a Creator** | `POST /auth/set-username` | AuthController | `[Authorize]` |
| **Get own Creator profile (UUID)** | `GET /api/creator/me` | CreatorsController | `[Authorize][RequireCreatorTier]` |
| **Update Creator profile (UUID)** | `PUT /api/creator/me` | CreatorsController | `[Authorize][RequireCreatorTier]` |
| **Get own CreatorProfile (slug)** | `GET /creator-profile/me` | CreatorProfileController | `[Authorize]` |
| **Upsert CreatorProfile (slug)** | `PUT /creator-profile/me` | CreatorProfileController | `[Authorize]` ⚠️ |
| **Patch profile settings** | `PATCH /creator-profile/me/settings` | CreatorProfileController | `[Authorize]` ⚠️ |
| **Upload banner** | `POST /creator-profile/me/banner` | CreatorProfileController | `[Authorize]` ⚠️ |
| **Upload avatar** | `POST /creator-profile/me/avatar` | CreatorProfileController | `[Authorize]` ⚠️ |
| **Upload avatar (settings)** | `POST /settings/profile/avatar` | CreatorProfileController | `[Authorize]` ⚠️ |
| **Presigned image URL** | `POST /api/uploads/creator-image-url` | CreatorsController | `[Authorize][RequireCreatorTier]` |
| **Proxy image upload** | `PUT /api/uploads/creator-image/{key}` | CreatorsController | `[Authorize]` ⚠️ |
| **Multipart image upload** | `POST /api/uploads/creator-image` | CreatorsController | `[Authorize][RequireCreatorTier]` |
| **Public profile (slug)** | `GET /creator-profile/{slug}` | CreatorProfileController | Public |
| **Public storefront** | `GET /creator-profile/{slug}/storefront` | CreatorProfileController | Public, feature-flagged |
| **Public profile (UUID)** | `GET /api/creators/{id}` | CreatorsController | Public |
| **Public profile (username)** | `GET /api/creators/by-username/{u}` | CreatorsController | Public |
| **Resolve identifier** | `GET /api/creators/resolve/{id}` | CreatorsController | Public |
| **Public tracks (UUID)** | `GET /api/creators/{id}/tracks` | CreatorsController | Public |
| **Public tracks (slug)** | `GET /creator/tracks/{slug}` | CreatorsController | Public |
| **Username availability** | `GET /api/creators/username-availability` | CreatorsController | Public, rate-limited |
| **Username availability** | `GET /auth/username-availability` | AuthController | Public, rate-limited |
| **Collections list** | `GET /creator-profile/{slug}/collections` | CreatorProfileController | Public |
| **Create collection** | `POST /creator-profile/me/collections` | CreatorProfileController | `[Authorize][RequireCreatorTier][RequireUsername]` |
| **Update collection** | `PUT /creator-profile/me/collections/{id}` | CreatorProfileController | `[Authorize][RequireCreatorTier][RequireUsername]` |
| **Delete collection** | `DELETE /creator-profile/me/collections/{id}` | CreatorProfileController | `[Authorize][RequireCreatorTier][RequireUsername]` |
| **Update pinned tracks** | `PUT /creator-profile/me/pinned-tracks` | CreatorProfileController | `[Authorize][RequireCreatorTier][RequireUsername]` |
| **Follow (UUID)** | `POST /api/creators/{id}/follow` | CreatorsController | `[Authorize]` |
| **Unfollow (UUID)** | `DELETE /api/creators/{id}/follow` | CreatorsController | `[Authorize]` |
| **Follow status (UUID)** | `GET /api/creators/{id}/follow` | CreatorsController | `[Authorize]` |
| **Follow (slug)** | `POST /creator-profile/{slug}/follow` | CreatorProfileController | `[Authorize]` |
| **Unfollow (slug)** | `DELETE /creator-profile/{slug}/follow` | CreatorProfileController | `[Authorize]` |
| **Follow status (slug)** | `GET /creator-profile/{slug}/follow` | CreatorProfileController | `[Authorize]` |
| **Creator dashboard tracks** | `GET /creator/tracks` | CreatorController | `[Authorize][RequireCreatorTier][RequireUsername]` |
| **Creator revenue** | `GET /creator/revenue` | CreatorController | `[Authorize][RequireCreatorTier][RequireUsername]` |
| **Edit track** | `PUT /creator/tracks/{id}` | CreatorController | `[Authorize][RequireCreatorTier][RequireUsername]` |
| **Upload track** | `POST /upload` | UploadController | `[Authorize][RequireCreatorTier][RequireUsername]` |
| **Generic image upload** | `POST /uploads/image` | UploadController | `[Authorize]` |
| **Admin verify creator** | `POST /admin/users/{id}/verify-creator` | AdminController | `[Authorize(Roles="Admin")]` |
| **Admin set role** | `POST /admin/users/{id}/role` | AdminController | `[Authorize(Roles="Admin")]` |
| **Admin upgrade tier** | `POST /admin/users/{id}/upgrade-tier` | AdminController | `[Authorize(Roles="Admin")]` |
| **Admin suspend** | `POST /admin/users/{id}/suspend` | AdminController | `[Authorize(Roles="Admin")]` |
| **Settings profile** | `GET /settings/profile` | AuthController | `[Authorize]` |
| **Update display name** | `PUT /auth/display-name` | AuthController | `[Authorize]` |

⚠️ = Missing `[RequireCreatorTier]` — see findings F1-F3.

---

### B — Missing Features / Gaps

1. **No profile deletion endpoint** — Creators cannot delete their CreatorProfile. Only admin can suspend/remove tracks. No self-service account deletion.
2. **No creator deactivation** — When admin suspends a user, their creator profile and storefront remain publicly accessible. No concept of "deactivated storefront."
3. **No slug immutability** — Username is immutable once set, but slug can be changed on every `PUT /creator-profile/me` call. No rate limit or history. A creator could squat multiple slugs over time.
4. **No old slug redirect** — When a creator changes their slug, the old slug becomes 404. No redirect mapping for bookmarked URLs.
5. **No DisplayName sync from Creator to CreatorProfile** — `CreatorProfileDto.DisplayName` is populated from the Creator table at read time (line 135 of CreatorProfileRepository), but a `PUT /creator-profile/me` never writes to Creator.DisplayName. Similarly, a `PUT /api/creator/me` that updates DisplayName does not update any CreatorProfile field.
6. **No verified creator badge on public profile** — `ApplicationUser.VerifiedCreator` exists and can be set by admin, but no public endpoint or DTO includes it.
7. **No niche field on Creator table** — `Niche` exists on CreatorProfile but not Creator. The Creator-facing endpoints lose this data.

---

### C — Authorization Gaps

| Endpoint | Current | Expected | Finding |
|----------|---------|----------|---------|
| `PUT /creator-profile/me` | `[Authorize]` | `[Authorize][RequireCreatorTier][RequireUsername]` | F1 |
| `PATCH /creator-profile/me/settings` | `[Authorize]` | `[Authorize][RequireCreatorTier]` | F2 |
| `POST /creator-profile/me/banner` | `[Authorize]` | `[Authorize][RequireCreatorTier]` | F3 |
| `POST /creator-profile/me/avatar` | `[Authorize]` | `[Authorize][RequireCreatorTier]` | F3 |
| `POST /settings/profile/avatar` | `[Authorize]` | `[Authorize][RequireCreatorTier]` | F3 |
| `PUT /api/uploads/creator-image/{key}` | `[Authorize]` | `[Authorize][RequireCreatorTier]` | F5 |
| Follow endpoints (all 6) | `[Authorize]` | `[Authorize]` — OK, any user can follow | — |

---

### D — Transaction / Data Consistency Risks

1. **F19** — `POST /auth/set-username` updates Identity then Creator table without a transaction. Failure of second write creates a stuck state.
2. **F6** — Dual-table image sync at controller level without transaction. Failure of sync call leaves tables inconsistent.
3. **F12/F13** — Legacy vs UUID FK divergence. Storefront uses legacy `CreatorId`; creator dashboard uses `CreatorUuid`. During migration, tracks may be invisible on one surface.
4. **Collection/pinned track ownership** — Uses legacy `CreatorId` (F15). Tracks with UUID-only FK cannot be added to collections.

---

### E — Storage / Cleanup Risks

1. **Orphaned images** — Every image upload creates a new S3 key. Old images are never deleted. Over time, storage accumulates unused files.
2. **Placeholder profiles** — F3's auto-create generates CreatorProfile rows with truncated-userId slugs. These may never be properly filled out.
3. **No old image cleanup on profile update** — When a creator uploads a new avatar, the old file remains in storage.

---

### F — Contract / Model Drift

1. **Two parallel stats models** — `CreatorStatsResponseDto` (from CreatorIdentityRepository) has `TrackCount`, `TotalSales`, `TotalDownloads`, `AverageRating`, `FollowerCount`. `CreatorStatsDto` (from CreatorProfileRepository) has `TotalDownloads`, `TotalEarnings`. Different shapes, computed differently.
2. **Slug vs Username** — Not distinguished in the frontend contract. A creator may have `username=john` but `slug=john-music`. The storefront falls back from slug to username lookup, but the reverse doesn't happen.
3. **`PublicCreatorDto.UserId`** is exposed — This leaks the internal ApplicationUser.Id to public API consumers via `GET /api/creators/{id}` and all UUID endpoints.

---

### G — Triage Order (recommended fix priority)

| Priority | Finding | Rationale |
|----------|---------|-----------|
| 1 (Critical) | **F19** | Stuck state — user promoted but no Creator row. Cannot re-run set-username. Requires manual DB fix. |
| 2 (High) | **F1, F2, F3** | Authorization gap — non-creators can create profiles and claim slugs. Breaks role separation. |
| 3 (High) | **F12** | Storefront uses legacy FK; new tracks may be invisible. Affects production correctness. |
| 4 (High) | **F11** | Admin role promotion creates half-state. Admin workflow gap. |
| 5 (Medium) | **F6** | Dual-table sync without transaction. Data drift under failure conditions. |
| 6 (Medium) | **F7** | Slug has no character/pattern validation. URL-unsafe slugs possible. |
| 7 (Medium) | **F9** | Pinned tracks skip ownership check. Low practical impact (filtered at render) but inconsistent. |
| 8 (Low) | **F4, F5, F8** | Magic bytes missing on generic upload; proxy key not bound; self-follow. |
| 9 (Low) | **F10, F13-F18, F20** | Perf, cosmetic, or migration-phase issues. |

---

## Explicit Questions — Answers

### Q1. Can a non-owner modify a creator profile anywhere?

**No, with qualifiers.** All write endpoints under `CreatorProfileController` use `GetRequiredUserId()` to scope writes to the calling user's own profile. The ownership is enforced by `userId` extracted from the JWT. However, the **authorization gate** is too weak (F1-F3): a user who is *not a creator* (Role=User) can create/modify a CreatorProfile. They cannot modify another user's profile, but they can create their own profile in a table that should be restricted to creators.

Admin can modify creator state indirectly via `POST /admin/users/{id}/role`, `POST /admin/users/{id}/verify-creator`, and `POST /admin/users/{id}/suspend`, but cannot directly edit profile fields (bio, slug, images). There is no admin endpoint to edit creator profile content.

### Q2. Are username/slug uniqueness and normalization safe?

**Mostly, with gaps.**
- **Username:** Unique across both `AspNetUsers.UserName` (Identity) and `Creators.Username` (DB unique index). Normalized to lowercase at input. Regex enforced: `^[a-z0-9_-]+$`. Immutable once set. Safe.
- **Slug:** Unique index on `CreatorProfiles.Slug`. Normalized to lowercase at `PUT /creator-profile/me` controller level. **No regex validation** (F7). The `UpdateImageAsync` auto-create generates placeholders from truncated userId — these occupy valid slug space. Slug is **mutable** (can be changed on every PUT). No collision between slug and username namespaces — they live in different tables and can diverge.
- **Cross-table uniqueness gap:** A username `john` in the Creators table and a slug `john` in CreatorProfiles can belong to different users. The storefront resolves by slug first, then falls back to username. This means `GET /creator-profile/john` returns user A's profile (slug owner), not user B's (username owner).

### Q3. Can a user upload an image to another user's profile?

**No.** All image upload endpoints use `GetRequiredUserId()` and scope the update to `userId`. The proxy upload (F5) can write to any S3 key under `creator-profiles/` or `creator-covers/`, but the key contains a random UUID, so practical exploitation requires guessing the exact key. The actual profile _update_ (`UpdateImageUrlAsync`, `UpdateImageAsync`) is always scoped to the authenticated user's ID.

### Q4. What happens if the Creator table upsert fails during `set-username`?

**Stuck state (F19).** The Identity update (Role=Creator, UserName=chosen) has already been committed via `UserManager.UpdateAsync()`. If `_creators.UpsertAsync()` fails with a non-constraint exception, the error is caught, logged as "Non-critical", and execution continues. The user receives a success response with the new token. They now have Role=Creator and a username but no Creator row. Re-calling `set-username` returns "Username cannot be changed once set." The user is stuck — they need manual DB intervention or a separate repair endpoint.

### Q5. Is the storefront safe for suspended users?

**No.** There is no check for `ApplicationUser.Status == "suspended"` in any public profile endpoint (`GET /creator-profile/{slug}`, `GET /{slug}/storefront`, `GET /api/creators/{id}`). A suspended user's profile, tracks, and collections remain publicly visible. The `[RequireCreatorTier]` attribute does not check suspension status either — it only checks Role. This may be a deliberate policy choice (suspend auth, not content), but it's undocumented.

### Q6. Can the dual Creator/CreatorProfile tables get out of sync?

**Yes (F6).** Image URLs are synced at the controller level post-write, not inside a transaction. If the sync call fails, the tables diverge. The read path in `MapToDtoAsync` (CreatorProfileRepository line 137-138) partially mitigates this by falling back: `p.ProfileImageUrl ?? creator?.ProfileImageUrl`. But the reverse (Creator table reading from CreatorProfile) does not have a fallback. Bio, SocialLinks, and other fields are independently maintained in each table and never synced.

### Q7. Do the UUID-based and legacy FK paths return consistent data?

**No (F12, F13).** The storefront (`GetStorefrontTracksAsync`) filters by `t.CreatorId` (legacy string FK). The UUID creator profile (`GetTracksByCreatorIdAsync`) filters by `t.CreatorUuid`. If a track only has one FK populated, it appears on one surface but not the other. Stats computations also diverge — `CreatorProfileRepository` counts by legacy FK; `CreatorIdentityRepository` counts by UUID FK.

### Q8. Is the username regex consistent across all validation points?

**Partially.** There are three validation points:
1. `POST /auth/set-username` (AuthController L249): `^[a-z0-9_-]+$` — allows leading/trailing hyphens/underscores.
2. `PUT /api/creator/me` (CreatorsController L259): delegates to `IsUsernameTakenAsync` after length check, no regex.
3. `UpdateCreatorProfileRequest` DTO (CreatorRequests.cs L10): `^[a-z0-9][a-z0-9\-]*[a-z0-9]$` — requires alphanumeric start/end, no underscores.

The DTO regex and the AuthController regex are **different**. The DTO disallows underscores and leading/trailing hyphens; AuthController allows both. Since `PUT /api/creator/me` uses the DTO, a username with underscores set via `POST /auth/set-username` would be rejected on update — but since username is immutable once set, this is unlikely to cause issues in practice. Still an inconsistency.

### Q9. Can a creator follow themselves?

**Yes (F8).** No endpoint checks `userId == creator.UserId` before calling `FollowAsync`. The DB unique index on `(FollowerId, CreatorId)` prevents double-follows but not self-follows.

### Q10. Are pinned tracks validated for ownership?

**No (F9).** `PUT /creator-profile/me/pinned-tracks` saves `body.TrackIds` directly. Collections CRUD validates via `AllTracksOwnedByCreatorAsync`, but pinned tracks does not. The impact is mitigated because `ResolvePinnedTracks` in StorefrontService only shows tracks already in the creator's track list.

### Q11. Is there any rate limiting on profile creation/modification?

**Yes, partially.** `CreatorProfileController` has `[EnableRateLimiting("auth")]` at class level (10 req/min in production). `CreatorsController` does NOT have a class-level rate limit — individual endpoints like `username-availability` have it, but `PUT /api/creator/me`, follow endpoints, and track listing are unprotected. Follow endpoints in particular could be abused to spam follow/unfollow.

### Q12. Does admin verification (`VerifiedCreator`) propagate to public API responses?

**No.** `VerifiedCreator` is a boolean on `ApplicationUser`. No public-facing DTO (`PublicCreatorDto`, `CreatorProfileDto`, `StorefrontResponse`) includes this field. The admin can set it via `POST /admin/users/{id}/verify-creator`, but there is no way for the frontend or API consumers to see whether a creator is verified.
