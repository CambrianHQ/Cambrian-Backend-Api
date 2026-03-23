# Creator Identity — Rollout & Rollback Plan

> **Strategy**: expand → backfill → backend deploy → observe → frontend deploy → gradual enablement  
> **Principle**: Additive schema changes only. No destructive migrations. Legacy routes stay live until switched off.  
> **Canonical internal key**: `creator.id` (UUID)  
> **Canonical public key**: `username`  
> **Email**: auth-only — never appears in public APIs

---

## Migration Plan

### Migration 1: `20260322000000_AddCreatorsIdentityTable`

| Step | Action | Destructive? |
|------|--------|:---:|
| 1 | Create `Creators` table (UUID PK, unique `UserId`, unique `Username`) | No |
| 2 | Add `CreatorUuid` nullable FK column to `Tracks` | No |
| 3 | Backfill: create `Creator` rows for all creator-tier users + users with tracks | No |
| 4 | Backfill: deduplicate usernames with `-N` suffix | No |
| 5 | Backfill: set `Track.CreatorUuid` from `CreatorId → Creator.UserId` mapping | No |

### Migration 2: `20260323000000_SeedCreatorIdentityFeatureFlags`

| Step | Action | Destructive? |
|------|--------|:---:|
| 1 | Seed `creator_profiles` flag (disabled, 0% rollout) | No |
| 2 | Seed `username_routing` flag (disabled, 0% rollout) | No |

Both use `ON CONFLICT DO NOTHING` — safe to re-run.

---

## Rollout Steps

### Step 1: Deploy backend to staging

1. Run both migrations (auto-applied on startup for non-Testing environments)
2. New endpoints go live with feature flags disabled:
   - `GET /api/creators/{creatorId}` — UUID lookup
   - `GET /api/creators/by-username/{username}` — username resolution
   - `GET /api/creators/{creatorId}/tracks` — UUID-filtered tracks
   - `GET /api/creators/resolve/{identifier}` — **compatibility resolver** (accepts UUID, ApplicationUser.Id, or username)
   - `GET /api/creators/username-availability` — availability check
   - `PUT /api/creator/me` — create/update identity (auth required)
   - `POST /api/uploads/creator-image-url` — image upload (auth required)
3. Legacy `/creator-profile/*` endpoints remain unchanged and active
4. Email leaks removed from `StreamController`, `StreamService`, `CatalogService`, `PurchaseService`, `StorefrontService`, `AuthService`
5. Structured logging enabled for:
   - Unresolved creator lookups (`CreatorLookup`, `CreatorResolve`)
   - Duplicate username attempts (`DuplicateUsername`)
   - Zero-track mismatches (`ZeroTrackMismatch` — creator has legacy tracks but no UUID-linked tracks)
   - Legacy identifier resolution (`LegacyResolve`)

### Step 2: Staging verification

Run all checks from **Production Checks** section below.

### Step 3: Deploy backend to production

Same as staging. Migrations auto-apply. Feature flags remain disabled.

### Step 4: Observe (24–48 hours)

- Monitor logs for `ZeroTrackMismatch` warnings (indicates incomplete backfill)
- Monitor logs for `CreatorLookup` 404s (indicates broken links)
- Monitor logs for `LegacyResolve` calls (indicates frontend still using old IDs)
- Verify no elevated error rates on existing endpoints
- Confirm payments, audio streaming, downloads are unaffected

### Step 5: Deploy frontend

- Frontend uses `/api/creators/resolve/{identifier}` for any legacy links
- Frontend uses `/api/creators/by-username/{username}` for new creator pages
- New creator UI gated behind feature flags

### Step 6: Gradual enablement

```
# 10% rollout
PUT /feature-flags/creator_profiles  { "enabled": true, "rolloutPercentage": 10 }
PUT /feature-flags/username_routing  { "enabled": true, "rolloutPercentage": 10 }

# Monitor 24 hours, then 50%
PUT /feature-flags/creator_profiles  { "enabled": true, "rolloutPercentage": 50 }
PUT /feature-flags/username_routing  { "enabled": true, "rolloutPercentage": 50 }

# Monitor 24 hours, then 100%
PUT /feature-flags/creator_profiles  { "enabled": true, "rolloutPercentage": 100 }
PUT /feature-flags/username_routing  { "enabled": true, "rolloutPercentage": 100 }
```

---

## Production Checks

### Canary verification (3 real creators)

For 3 known canary creators, verify:

```sql
-- 1. DB: get creator + track count
SELECT c."Id", c."Username", c."DisplayName",
       (SELECT COUNT(*) FROM "Tracks" t WHERE t."CreatorUuid" = c."Id") AS uuid_tracks,
       (SELECT COUNT(*) FROM "Tracks" t WHERE t."CreatorId" = c."UserId") AS legacy_tracks
FROM "Creators" c
WHERE c."Username" IN ('canary1', 'canary2', 'canary3');
```

Then via API:
```
GET /api/creators/{creatorId}           → verify username, displayName match DB
GET /api/creators/by-username/{username} → verify same creator returned as by UUID
GET /api/creators/{creatorId}/tracks     → verify track count matches uuid_tracks from DB
```

If `uuid_tracks != legacy_tracks`, run the backfill fix:
```sql
UPDATE "Tracks" t SET "CreatorUuid" = c."Id"
FROM "Creators" c WHERE t."CreatorId" = c."UserId" AND t."CreatorUuid" IS NULL;
```

### Email exposure check

```
# No @ in any public API response
curl -s /api/creators/{id} | grep -c '@'           # must be 0
curl -s /api/creators/{id}/tracks | grep -c '@'    # must be 0
curl -s /discover | grep -c '@'                     # must be 0
```

### Backfill completeness

```sql
-- Verify zero orphaned creators
SELECT COUNT(*) FROM "AspNetUsers" u
WHERE u."Tier" = 'creator'
  AND NOT EXISTS (SELECT 1 FROM "Creators" c WHERE c."UserId" = u."Id");
-- Expected: 0

-- Verify zero un-migrated tracks
SELECT COUNT(*) FROM "Tracks" WHERE "CreatorUuid" IS NULL;
-- Expected: 0

-- Verify no duplicate usernames
SELECT "Username", COUNT(*) AS cnt FROM "Creators"
GROUP BY "Username" HAVING COUNT(*) > 1;
-- Expected: 0 rows
```

### Compatibility resolver check

```
# Legacy ApplicationUser.Id resolves to same creator as UUID
GET /api/creators/resolve/{applicationUserId} → note returned creator.id
GET /api/creators/{creatorId}                  → same creator
```

### Payments and audio

```
# Purchases still work
POST /checkout  { trackId, licenseType: "non_exclusive" }  → 200
# Audio streaming still works
GET /stream/{trackId}/audio  → 302 to signed URL
# Downloads still work
GET /downloads/{purchaseId}  → 200
```

---

## Risk List

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|:---:|:---:|------------|
| 1 | Incomplete backfill — some tracks have `CreatorUuid = NULL` | Medium | High | `ZeroTrackMismatch` logging detects this; manual SQL backfill fix available |
| 2 | Duplicate usernames after backfill (deduplication suffix breaks links) | Low | Medium | Backfill migration handles duplicates with `-N` suffix; `DuplicateUsername` logging on upsert |
| 3 | Legacy frontend sends ApplicationUser.Id instead of UUID | High | Low | Compatibility resolver (`/api/creators/resolve/{identifier}`) accepts both formats |
| 4 | Email leaks reintroduced in future code | Low | High | `detect-email-leaks.cjs` CI script blocks PRs; 16 integration tests assert no email in responses |
| 5 | Feature flag rollout causes partial UI state | Medium | Low | Deterministic rollout (hash-based per user) — user sees consistent state |
| 6 | Migration fails on large dataset | Low | High | Migration uses `ON CONFLICT DO NOTHING`; safe to re-run. Down() migration drops cleanly. |
| 7 | Stats show 0 downloads/rating/followers (placeholder values) | Expected | Low | Documented as Phase 2 backlog. UI should handle 0 gracefully. |
| 8 | DisplayName null for old creators | Medium | Low | Falls back to username in all public APIs. `GetDisplayNameAsync` no longer returns email. |

---

## Rollback Steps

### If issues detected in Phase 1 (after deploy, flags off)

New endpoints are live but unused by frontend (flags disabled). **No rollback needed** — just don't enable the flags.

### If issues detected in Phase 2 (flags partially enabled)

1. **Disable feature flags immediately**:
   ```
   PUT /feature-flags/creator_profiles  { "enabled": false, "rolloutPercentage": 0 }
   PUT /feature-flags/username_routing  { "enabled": false, "rolloutPercentage": 0 }
   ```
2. Frontend falls back to legacy `/creator-profile/*` endpoints
3. New API endpoints continue to exist but receive no traffic
4. Investigate and fix the issue, then re-enable flags

### If code rollback is needed

1. Deploy previous release (git revert or rollback in Render)
2. **Do NOT run `Down()` on the migration** — the `Creators` table and `CreatorUuid` column are safe to leave in place
3. Legacy code does not reference these columns, so schema is forward-compatible
4. When ready, re-deploy the fix

### Data rollback (nuclear option — avoid if possible)

If the `Creators` table or `CreatorUuid` data is corrupted:

```sql
-- Reset CreatorUuid on all tracks
UPDATE "Tracks" SET "CreatorUuid" = NULL;

-- Truncate Creators table
TRUNCATE "Creators" CASCADE;

-- Re-run backfill
-- (use the SQL from the migration's Up() method)
```

---

## CI Enforcement

| Check | Script | Runs on |
|-------|--------|---------|
| Email leak detection | `node scripts/detect-email-leaks.cjs` | Every PR |
| Contract validation | `node scripts/validate-contracts.cjs` | Every PR |
| Integration tests | `dotnet test --filter "CreatorIdentity"` | Every PR |

---

## Files Changed

| File | Change |
|------|--------|
| `src/Cambrian.Domain/Entities/Creator.cs` | First-class creator entity with UUID PK |
| `src/Cambrian.Domain/Entities/Track.cs` | `CreatorUuid` nullable FK + `CreatorEntity` navigation |
| `src/Cambrian.Application/Interfaces/ICreatorIdentityRepository.cs` | Interface + `ResolveByLegacyIdentifierAsync` |
| `src/Cambrian.Application/DTOs/Creators/PublicCreatorDto.cs` | Public DTO — no email field |
| `src/Cambrian.Application/DTOs/Creators/CreatorRequests.cs` | Request/response DTOs — no email field |
| `src/Cambrian.Persistence/Repositories/CreatorIdentityRepository.cs` | Repository + ILogger + `ResolveByLegacyIdentifierAsync` + `ZeroTrackMismatch`/`DuplicateUsername` logging |
| `src/Cambrian.Persistence/CambrianDbContext.cs` | Creator entity config + CreatorUuid FK on Track |
| `src/Cambrian.Persistence/Migrations/20260322000000_AddCreatorsIdentityTable.cs` | Backfill migration |
| `src/Cambrian.Persistence/Migrations/20260323000000_SeedCreatorIdentityFeatureFlags.cs` | Feature flag seeding |
| `src/Cambrian.Api/Controllers/CreatorsController.cs` | 7 endpoints, ILogger, structured logging |
| `src/Cambrian.Api/Controllers/StreamController.cs` | Email leak removed |
| `src/Cambrian.Application/Services/StreamService.cs` | Email leak removed |
| `src/Cambrian.Application/Services/CatalogService.cs` | Email leak removed |
| `src/Cambrian.Application/Services/PurchaseService.cs` | Email leak removed |
| `src/Cambrian.Application/Services/StorefrontService.cs` | Email leak removed |
| `src/Cambrian.Application/Services/AuthService.cs` | Email leak removed (display name recovery) |
| `src/Cambrian.Api/Program.cs` | DI registration for `ICreatorIdentityRepository` |
| `tests/Cambrian.Api.Tests/CreatorIdentityTests.cs` | 16 integration tests |
| `tests/Cambrian.Api.Tests/CatalogServiceTests.cs` | Updated email fallback test |
| `tests/Cambrian.Api.Tests/Fixtures/CambrianApiFixture.cs` | `SeedCreatorAsync`, `SeedTrackWithCreatorUuidAsync` helpers |
| `contracts/endpoint-manifest.v1.json` | 7 new endpoint entries |
| `contracts/openapi.v1.json` | 7 new paths + 6 new schemas |
| `scripts/detect-email-leaks.cjs` | CI email-leak analyzer |
