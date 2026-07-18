# Production playback flow map

Verified against source on 2026-07-18 (backend working tree on
`claude/p0-production-reliability-backend`; frontend repo `c:\Users\logan\cambrian`,
branch `claude/growth-events-frontend`). Production probes were run against
`https://cambrian-backend-api.onrender.com` and `https://cambrianmusic.com` the same
day. Companion design doc: `docs/PRODUCTION_AUDIO_PLAYBACK_CONTRACT.md`.

## End-to-end chain

```text
Track page (Next.js)
  app/track/[id]/page.tsx — ISR fetch, revalidate: 300
→ player state
  app/hooks/useTrackedAudioPlayback.ts — HTMLAudioElement wrapper
  audio.src = track.audioUrl from the cached DTO, or getAudioUrl(id)
  (app/lib/media.ts:28-30 → "/stream/{trackId}/audio", un-ticketed)
→ playback API (hardened path, not yet consumed by the frontend)
  GET /api/v1/tracks/{trackId}/playback
  src/Cambrian.Api/Controllers/v1/PlaybackV1Controller.cs:35
→ authorization
  src/Cambrian.Persistence/Services/PlaybackAccessService.cs:47 (shared
  ITrackVisibilityPolicy; inaccessible tracks are masked as 404)
→ database track lookup
  PlaybackAccessService.cs:40 (Tracks + TrackMedia via EF Core/Npgsql)
→ R2 object lookup
  TrackMedia.ObjectKey (stable key, e.g. tracks/{creatorId}/{guid}.ext) —
  validated by src/Cambrian.Infrastructure/Validation/MediaValidationService.cs:43
→ signed URL or media endpoint
  Short-lived HMAC application ticket, not a presigned R2 URL:
  src/Cambrian.Application/Services/PlaybackTicketService.cs:23
  Location: /stream/{trackId}/audio?ticket=… (PlaybackV1Controller.cs:85)
→ browser audio request
  GET/HEAD /stream/{trackId}/audio — src/Cambrian.Api/Controllers/StreamController.cs
  proxies R2 via a 5-minute internal presigned URL that never reaches the client
  (src/Cambrian.Infrastructure/Storage/S3ObjectStorage.cs:218) and forwards Range
  end-to-end (S3ObjectStorage.cs:241, StreamController range echo)
→ playback events
  POST /stream/start · POST /stream/stop (qualified-play ledger)
  POST /api/v1/playback/telemetry (browser media events)
  src/Cambrian.Api/Controllers/v1/PlaybackTelemetryV1Controller.cs
```

## Part 1 questions, answered with citations

**Where playback URLs are generated.** Every mainstream catalog surface rewrites the
stored object key to a proxy URL before responding: `CatalogController.cs:215-227`
(`/discover`, `/catalog`, `/tracks`, `/track/{id}`, `/trending`),
`LibraryController.cs:32`, `CreatorProfileController.cs:125`, `CreatorsController.cs:662`,
`PublicUrlResolver.cs:48` (config-based, used by `api/public`),
`TrackAiResponseBuilder.cs:174` (AI discovery, relative path). The hardened issuer is
`PlaybackV1Controller.cs:85` (ticketed absolute URL). The legacy authenticated
`GET /stream/{trackId}` also issues a ticketed URL now (`StreamController.cs:126`).

**Where they are cached.**
- Backend: `CatalogController.cs:47-52` caches list DTOs in `IMemoryCache` for 15 s.
  `PublicController.cs:36` marks `api/public` responses `[ResponseCache]` public/15 s
  (those embed only stable proxy URLs, never tickets).
- Frontend: track pages are ISR-cached 300 s with `track.audioUrl` serialized into
  the RSC payload and into OpenGraph/JSON-LD audio tags
  (`app/track/[id]/page.tsx:28-29, 96-102, 134, 168`); home/explore/genre/scene pages
  edge-cache catalog DTOs containing `audioUrl` (`app/lib/serverCatalog.ts:32-65`).
- Cloudflare: audio responses are `cf-cache-status: DYNAMIC` (never edge-cached),
  confirmed by production probe 2026-07-18.

**URL expiration.** Playback tickets: `PlaybackMediaOptions.TicketLifetimeMinutes`
(short-lived HMAC, `PlaybackTicketService.cs:27`). Presigned storage URLs exist only
on entitlement-gated non-playback paths: downloads 15–30 min
(`S3ObjectStorage.cs:153,170`), internal proxy reads 5 min (`S3ObjectStorage.cs:218`),
upload PUTs 15 min (`S3ObjectStorage.cs:199`). Anonymous playback never receives a
presigned URL.

**Whether URLs are stored in PostgreSQL.** `Tracks.AudioUrl` stores the raw storage
object key, not a signed URL (`UploadService.cs:318`, `S3ObjectStorage.cs:142`
returns the bare key). The hardened pipeline stores the authoritative key in
`TrackMedia.ObjectKey`. A historical bug that persisted presigned URLs was scrubbed
by migration `20260407120000_FixExpiredPresignedImageUrls.cs:58`. `Library` rows keep
a denormalized copy of the key (`LibraryService.cs:78`) which `LibraryController.cs:32`
rewrites to proxy URLs. Signed URLs and tickets are never persisted.

**Whether URLs are serialized into cached HTML.** Yes — but only stable, non-expiring
proxy URLs (`/stream/{id}/audio`): frontend ISR pages and RSC payloads
(`app/track/[id]/page.tsx:96-102`), OG/JSON-LD audio tags, and the edge-cached
catalog payloads. Verified in production: `https://cambrianmusic.com/@smiles2322`
HTML embeds `https://cambrian-backend-api.onrender.com/stream/{id}/audio` and the
page itself is served `Cache-Control: private, no-cache, no-store` (probe
2026-07-18). No expiring URL is ever in cached HTML today; once ticket enforcement
turns on, those embedded un-ticketed URLs must be replaced by gesture-time playback
fetches (frontend work item).

**Whether Next.js caches playback API responses.** The playback endpoint is not yet
called by the frontend. Track DTO fetches use `next: { revalidate: 300 }`
(`app/track/[id]/page.tsx:28-29`) and `serverCatalog.ts` edge caching. The playback
client must use `cache: 'no-store'` (backend also sends `private, no-store` —
`PlaybackV1Controller.cs:102-108`).

**Whether Cloudflare caches signed URLs.** No. Audio and API responses probe as
`cf-cache-status: DYNAMIC`; ticketed responses additionally send
`Cache-Control: private, no-store` (`StreamController.ApplyPlaybackResponseHeaders`).
There are no Cloudflare Workers, `_headers`, or wrangler files in the backend repo.

**Whether cookies are required.** No. `GET /api/v1/tracks/{id}/playback` and
`GET/HEAD /stream/{id}/audio` are `[AllowAnonymous]`; the ticket travels as a query
parameter. The only cookie in the subsystem is the optional anonymous play-tracking
session (`StreamController.cs:463`), where the `X-Cambrian-Anonymous-Session` header
takes precedence. Production probes (anonymous, no cookies) returned 200/206 for all
60 published tracks.

**Whether multiple storage domains are in use.** One live storage path: providers
`s3` and `r2` both register `S3ObjectStorage` against the configured R2 endpoint
(`StartupExtensions.cs:120,147`; production bucket `cambrian-audio-prod`,
`appsettings.Production.json`). `R2ObjectStorage.cs:20` is a dead, never-registered
placeholder hardcoding `storage.cambrian.app`. The canonical public media host is the
backend itself (`https://cambrian-backend-api.onrender.com`). The frontend still
rewrites the dead `api.cambrianmusic.com` origin to the live backend
(`app/lib/media.ts:120,194-196`) and its CSP still allowlists
`*.r2.cloudflarestorage.com` (`public/_headers:16`) although nothing fetches R2
directly — both are frontend cleanup items.

**Legacy Supabase / old R2 paths.** The name "Supabase" survives only as the named
HttpClient `"SupabaseStorage"` used by the active S3 gateway (`StartupExtensions.cs:143`)
and a stale privacy-policy disclosure in the frontend (`views/PrivacyPage.tsx:77,183`).
Legacy absolute URLs stored in `Tracks.AudioUrl` are normalized to bare keys before
signing (`S3ObjectStorage.cs:557`), so the proxy never fetches an old host.

## Production probe evidence (2026-07-18, deployed build)

- `GET /stream/{id}/audio` → 200, `Content-Type: audio/mpeg`,
  `Content-Length: 3955293`, `Accept-Ranges: bytes`, full body downloads.
- `Range: bytes=0-1023` → 206, `Content-Range: bytes 0-1023/3955293`.
- All 60 published catalog tracks return 206 on anonymous ranged requests (0 failures).
- CORS: `access-control-allow-origin` echoes both `https://cambrianmusic.com` and
  `https://www.cambrianmusic.com`; preflight allows the `Range` header.
- Deployed-build defects the working tree fixes: `HEAD` → 405 (working tree adds
  `[HttpHead]`); out-of-range Range → 404 instead of `416 bytes */TOTAL` (working
  tree preserves origin 416, `S3ObjectStorage.cs:262`, `StreamController` 416 branch);
  no `Cache-Control` on audio (working tree always sends `private, no-store`).
