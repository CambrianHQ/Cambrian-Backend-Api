# MCP Backend Contract

This document is the contract between the Cambrian backend and the **public, read-only MCP
server**. It tells the MCP server author exactly what they can rely on.

> **The MCP server is not implemented in this backend.** This repository only exposes stable,
> safe, public HTTP endpoints. The MCP server is a separate process that consumes them and maps
> them to MCP tools/resources. Do not add MCP-server logic here.

## Base

- **Base URL:** `{API_ORIGIN}/api/public`
- **Auth:** none. Every endpoint is `GET` + `[AllowAnonymous]`. Do not send credentials.
- **Envelope:** `{ "success": true, "data": … }`. On error: `{ "success": false, "error": "…" }`.
- **Content type:** `application/json`.
- **Versioning:** this surface is additive-only. New fields may be added; existing fields and
  routes are stable. Treat unknown fields as optional.

## Endpoints → suggested MCP tools

| MCP intent | Endpoint |
| --- | --- |
| `search_tracks` | `GET /api/public/tracks/search?q=&genre=&mood=&tempo=&instrumental=&sort=&page=&pageSize=` |
| `get_track` | `GET /api/public/tracks/{trackId}` (`CAMB-TRK-XXXX` or UUID) |
| `search_creators` | `GET /api/public/creators/search?q=&page=&pageSize=` |
| `get_creator` | `GET /api/public/creators/{slug}` |
| `list_genres` | `GET /api/public/genres` |
| `get_genre` | `GET /api/public/genres/{genre}?page=&pageSize=` |
| `trending_tracks` | `GET /api/public/trending?genre=&mood=&tempo=&instrumental=&page=&pageSize=` |
| `latest_releases` | `GET /api/public/latest?page=&pageSize=` |
| `featured_creators` | `GET /api/public/featured-creators?limit=` |
| `platform_stats` | `GET /api/public/stats` |
| `pricing` | `GET /api/public/pricing` |
| `faq` | `GET /api/public/faq` |
| `sitemap` | `GET /api/public/sitemap` |
| `release_ready_info` | `GET /api/public/release-ready` |
| `authorship_info` | `GET /api/public/authorship` |
| `creator_guide` | `GET /api/public/creator-guide` |

Full request/response shapes are in `contracts/openapi.v1.json` (search for `/api/public/`) and
[`PUBLIC_API.md`](./PUBLIC_API.md). DTO field reference: [`SAFE_PUBLIC_DTO.md`](./SAFE_PUBLIC_DTO.md).

## What the MCP server can rely on

- **Safety:** responses contain only public data — no emails, no Stripe/payment data, no wallet or
  earnings, no raw storage keys, no drafts/hidden/removed content. Safe to surface verbatim to end
  users and to index.
- **Canonical URLs:** every resource carries an absolute `canonicalUrl` (and resources carry
  `relatedUrls`). Use these for citations/links. They are configured, https, and never localhost.
- **SEO/AI metadata:** `metaTitle`, `metaDescription`, `structuredDataType` (schema.org) on every
  resource; `tags` where relevant.
- **Freshness:** `updatedAt` / `lastModified` where known; `sitemap` provides last-modified per URL.
- **Real metrics only:** `plays`, `sales`, `followers`, `trackCount`, `provenanceStatus`,
  `aiGenerated`. The server should **not** expect `trendingScore`, `complianceScore`,
  `commentCount`, `repostCount`, or tips — these are intentionally absent (see `SAFE_PUBLIC_DTO.md`).

## Pagination & validation

- `page` ≥ 1, `pageSize` 1–**50** (values > 50 are clamped; the response echoes the effective
  `pageSize`). Out-of-range or non-numeric paging → **400**.
- Unknown track/creator/genre → **404**.
- List responses include `page`, `pageSize`, `totalCount`, `totalPages`, `hasNextPage`,
  `hasPreviousPage` — iterate with these.

## Caching & rate limits

- Discovery endpoints: `Cache-Control: public, max-age=300` (5 min).
- Evergreen content (pricing/faq/sitemap/content pages): `max-age=3600` (1 h).
- The MCP server should honor these and cache accordingly. The global API rate limiter applies;
  respect `429` with backoff.

## Stability contract

- Routes under `/api/public` are stable and additive-only.
- Field semantics will not change incompatibly; new optional fields may appear.
- `provenanceStatus` values are an ordered enum: `none` < `hashed` < `stamped` < `verified`.
- Money is integer **cents** (`priceCents`, `priceCentsMonthly`) plus a convenience decimal.

## Remaining MCP-server work (out of scope for this backend)

1. Implement the MCP server process (tools/resources) that calls these endpoints.
2. Map each endpoint to an MCP tool (see table above) and translate the `{success,data}` envelope.
3. Add client-side caching keyed on the `Cache-Control` max-age values.
4. Optionally consume `GET /api/public/sitemap` to pre-warm/crawl track & creator pages.
5. Configure the API origin and (if needed) `App:ApiBaseUrl` so media URLs resolve in production.
