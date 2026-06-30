# Public API (`/api/public`)

A stable, **read-only, unauthenticated** API surface for crawlable, SEO/AI-safe data about
Cambrian. It is the contract the public **MCP server** consumes — see
[`MCP_BACKEND_CONTRACT.md`](./MCP_BACKEND_CONTRACT.md). DTO safety rules are in
[`SAFE_PUBLIC_DTO.md`](./SAFE_PUBLIC_DTO.md).

> The MCP server is **not** implemented in this backend. This backend only exposes the
> endpoints below; the MCP server is a separate process that calls them.

## Guarantees

Every endpoint under `/api/public`:

- is **GET-only** and **`[AllowAnonymous]`** (no auth, no API key);
- returns only **public** data — excludes drafts/hidden/limited/removed/exclusive-sold tracks,
  raw storage keys, emails, Stripe/payment data, wallet/earnings, and admin/internal fields;
- **validates** query parameters (`page`/`pageSize` must be ≥ 1) and **clamps** page size to a
  hard maximum of **50**;
- sets **public cache headers** (`Cache-Control: public, max-age=…`);
- returns **canonical URLs**, **public image URLs**, **`updatedAt`/last-modified** where known,
  and SEO/structured-data metadata (`metaTitle`, `metaDescription`, `structuredDataType`).

Responses use the standard envelope: `{ "success": true, "data": … }`.

## Endpoints

| Method & path | Description | Cache |
| --- | --- | --- |
| `GET /api/public/tracks/search` | Search/filter the catalogue (`q`, `genre`, `mood`, `tempo`, `instrumental`, `sort`, `page`, `pageSize`) | 5 min |
| `GET /api/public/tracks/{trackId}` | Single track by `CAMB-TRK-XXXX` ID or UUID | 5 min |
| `GET /api/public/creators/search` | Creator directory search (`q`, `page`, `pageSize`) | 5 min |
| `GET /api/public/creators/{slug}` | Creator profile by storefront slug | 5 min |
| `GET /api/public/genres` | All genres with real track counts | 5 min |
| `GET /api/public/genres/{genre}` | Genre detail + a page of its public tracks | 5 min |
| `GET /api/public/trending` | Trending tracks (ranked by real lifetime plays) | 5 min |
| `GET /api/public/latest` | Latest releases (newest first) | 5 min |
| `GET /api/public/featured-creators` | Featured creators (ranked by # of public tracks) | 5 min |
| `GET /api/public/stats` | Aggregate platform stats | 5 min |
| `GET /api/public/pricing` | Plan pricing (no Stripe price IDs) | 1 h |
| `GET /api/public/faq` | FAQ (FAQPage structured data) | 1 h |
| `GET /api/public/sitemap` | Sitemap entries (pages + tracks + creators) | 1 h |
| `GET /api/public/release-ready` | Release Ready info page | 1 h |
| `GET /api/public/authorship` | Authorship info page | 1 h |
| `GET /api/public/creator-guide` | Creator Guide info page | 1 h |

### Query parameters

- `page` — 1-based; **< 1 → 400**.
- `pageSize` — default 20; **< 1 → 400**; **> 50 → clamped to 50** (the response echoes the
  effective `pageSize`).
- `q`, `genre`, `mood`, `tempo`, `sort` — optional strings.
- `instrumental` — optional boolean.
- `limit` (featured-creators) — clamped to 1–24.

Non-numeric paging values (e.g. `?page=abc`) are rejected with **400** by model binding.

## Real metrics only

Exposed because they are computed live from transactional tables:

- track **plays** (stream sessions), **sales** (completed purchases);
- track **provenanceStatus** (`none` → `hashed` → `stamped` → `verified`, derived from the §9
  hash/signature/attestation fields — never the raw hash or signature) and **aiGenerated**;
- creator **plays**, **followers**, **sales**, **trackCount**.

**Not exposed** because they are dead schema or don't exist: `trendingScore`
(`Track.TrendingScore` is never populated — there is no recompute job), `complianceScore` (not
implemented), `commentCount`/`repostCount`/`oneTimeTips` (no such data). Earnings/revenue and fan
subscription figures are intentionally excluded as financial/monetization data. See
[`SAFE_PUBLIC_DTO.md`](./SAFE_PUBLIC_DTO.md).

## URLs & configuration

URLs are built from configuration, **never** from the inbound request host, so production output
can never contain a localhost URL:

- `App:FrontendUrl` — public **site** base for canonical/SEO URLs (e.g.
  `https://cambrianmusic.com`). Defaults to `https://cambrianmusic.com` if unset.
- `App:ApiBaseUrl` — base for proxied **media** URLs (`/images/{key}`, `/stream/{id}/audio`).
  Defaults to `App:FrontendUrl` if unset. Set it to the API origin if the site does not proxy
  `/images` and `/stream` to the API.

Image URLs are always proxied through `/images/{key}` with the storage bucket origin/name
stripped; audio is exposed only as `…/stream/{trackId}/audio`. A raw storage key is never returned.

## Architecture

```
PublicController (HTTP only, [AllowAnonymous], ResponseCache)
  └─ IPublicApiService / PublicApiService          (orchestration + public-safe mapping)
       ├─ ICatalogService / CatalogService         (live track lists + real metrics)
       ├─ ICreatorProfileRepository                (creator profile + live stats)
       ├─ IPublicDirectoryRepository               (platform counts, genres, creator search,
       │      / PublicDirectoryRepository           featured creators, sitemap)
       ├─ IPublicUrlResolver / PublicUrlResolver   (canonical + safe media URLs from config)
       └─ PublicContentCatalog                     (curated FAQ / content pages)
```

The controller contains no business logic (governance: no LINQ / DbContext / domain entities).

## Validation

- Build: `dotnet build Cambrian.sln -c Release`
- Tests: `dotnet test … --filter "FullyQualifiedName~Cambrian.Api.Tests.Public"` (42 tests)
- OpenAPI generation: `dotnet swagger tofile --output contracts/openapi.v1.json src/Cambrian.Api/bin/Release/net8.0/Cambrian.Api.dll v1` (CI flow); the 16 public paths are present in `contracts/openapi.v1.json`.
- Contract: `node scripts/validate-contracts.cjs` (pass), `node scripts/check-contract-drift.cjs`
  (the public routes are drift-free; remaining failures are pre-existing).
