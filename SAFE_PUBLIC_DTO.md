# Safe Public DTOs

Rules and rationale for the DTOs returned by the public API (`/api/public/*`). These DTOs are the
**only** shapes the public MCP server / crawlers ever see. They live in
`src/Cambrian.Application/DTOs/Public/`.

## The golden rule

A public DTO is **allow-list**, not deny-list. A field appears only if it is safe to publish to
an anonymous crawler. When in doubt, leave it out.

## Common SEO base (`PublicSeoResource`)

Every crawlable resource carries:

| Field | Meaning |
| --- | --- |
| `title` | Human-readable title |
| `description` | Public snippet |
| `canonicalUrl` | Absolute canonical URL (built from `App:FrontendUrl`) |
| `imageUrl` | Absolute, proxied, public image URL (never a raw key) |
| `updatedAt` | Last-modified, when known |
| `tags` | Public tags |
| `relatedUrls` | Absolute URLs to related public resources |
| `metaTitle` / `metaDescription` | SEO hints |
| `structuredDataType` | schema.org type (`MusicRecording`, `MusicGroup`, `FAQPage`, …) |

List endpoints return `PublicListResponse<T>` (items + pagination + collection-level SEO,
`structuredDataType = "ItemList"`).

## DTOs

| DTO | Used by | Notes |
| --- | --- | --- |
| `PublicTrackDto` | track search/detail/trending/latest/genre | + `PublicCreatorRef` |
| `PublicCreatorDto` | creator profile | + `PublicCreatorStatsDto`, `PublicSocialLinkDto`, `RecentTracks` |
| `PublicCreatorSummaryDto` | creator search / featured | lightweight |
| `PublicGenreDto` / `PublicGenreDetailDto` | genres / genre detail | |
| `PublicPlatformStatsDto` | stats | |
| `PublicPricingDto` / `PublicPricingTierDto` | pricing | |
| `PublicFaqDto` / `PublicFaqItemDto` | faq | |
| `PublicContentPageDto` / `PublicContentSectionDto` | release-ready / authorship / creator-guide | |
| `PublicSitemapDto` / `PublicSitemapEntryDto` | sitemap | |

## What is exposed (and why it's safe)

- **Track:** id, `CAMB-TRK` id, title, description, genre/mood/tempo, instrumental, duration,
  **price** (cents + dollars), `audioPreviewUrl` (proxied `/stream/{id}/audio`), **plays**,
  **sales**, **aiGenerated**, **provenanceStatus**, `createdAt`, creator ref.
- **Creator:** id, slug, username, display name, bio, niche, public social links, **plays /
  followers / sales / trackCount**, recent public tracks.
- **Metrics are real** — computed live from `StreamSessions`, `Purchases` (status=completed), and
  `CreatorFollows`.
  - `provenanceStatus` is derived from `ContentHash` / `Signature` / `SignedAt` /
    `CommercialRightsVerified`: `none → hashed → stamped → verified`. The raw hash and signature
    are **never** returned.

## What is NEVER exposed

| Excluded | Why |
| --- | --- |
| Email, password/reset fields | Private user data |
| `StripeAccountId`, `cus_*`, `acct_*`, `price_*`, Stripe price config keys | Payment data |
| Wallet balance, **earnings/revenue**, payout data | Financial data |
| Raw storage keys, S3/R2 bucket origins/names | Replaced by proxied `/images` & `/stream` URLs |
| Platform-fee / creator-earnings breakdown | Internal money math (in `TrackResponse`, dropped here) |
| `CopyrightOwnerId`, `OriginalCreatorId`, copyright internals | Internal business logic |
| `AiDisclosureDdex` (raw payload) | Surfaced only as the `aiGenerated` boolean |
| Drafts / `Visibility != public` / removed / exclusive-sold | Non-public content |
| Admin/internal flags | Not public |

### Metrics deliberately omitted

- **`trendingScore`** — `Track.TrendingScore` is dead schema (always 0; no recompute job).
  Trending is computed from **real plays** instead.
- **`complianceScore`** — not implemented; no real per-track score exists.
- **`commentCount` / `repostCount` / `oneTimeTips`** — no comments, reposts, or one-time-tip data
  exist, so these fields are not invented.
- **Fan-subscription counts / revenue** — monetization-adjacent; excluded from the public surface.

## Banned positioning terms

Public responses must never contain retired positioning language. A test
(`PublicApiTests.PublicResponses_DoNotContainRetiredPositioningTerms`) asserts the absence of:

`marketplace`, `licensing marketplace`, `license marketplace`, `buyout`, `exclusive license`.

Curated copy in `PublicContentCatalog` is written in the current product voice (creator
legitimacy, provenance, Release Ready, subscriptions).

## Tests that enforce these rules

`tests/Cambrian.Api.Tests/Public/`:

- storage keys / emails / Stripe fields absent (`TrackDetail_DoesNotLeakStorageKeys_…`,
  `CreatorProfile_DoesNotLeakEmailOrStripeData`, `Pricing_…WithoutStripePriceIds`);
- drafts/hidden/limited/exclusive-sold excluded from search and detail (404);
- pagination clamped, invalid query rejected;
- cache headers present, canonical URLs absolute/https/no-localhost;
- banned terms absent;
- OpenAPI includes all public endpoints.
