# Cambrian Developer Docs

Welcome. This directory is the developer-facing home for the [Cambrian Backend API](https://api.cambrianmusic.com) — everything you need to integrate programmatic music licensing into your app.

If you're new here, start with the quickstart and then pick whichever tutorial matches what you're building.

---

## Start here

- **[5-minute quickstart](./quickstart.md)** — get an API key, list tracks, initiate a license, verify a license. All in curl.
- **[Interactive API explorer](./api-explorer.html)** — Swagger UI loaded against the live API. Paste your key and try requests in the browser.

## Tutorials

- **[Add licensed music to AI-generated video](./tutorials/add-licensed-music-to-ai-video.md)** — for AI video generation tools. Search by mood/tempo, pick a fitting track, and bind a license to every video your users export.
- **[Build a music-aware AI agent with MCP](./tutorials/build-music-aware-mcp-agent.md)** — for LLM agents. Connect Claude Desktop, Cursor, or the Anthropic SDK to Cambrian's MCP server and let the model reason over music search directly.
- **[License music programmatically in your content creation tool](./tutorials/license-music-programmatically.md)** — for podcast editors, video editors, presentation builders. Full end-to-end integration: browse, pick, license, verify, store proof.

## Reference

- **OpenAPI spec:** `https://api.cambrianmusic.com/openapi.json` (redirect to `/swagger/v1/swagger.json`)
- **Endpoint manifest:** `https://api.cambrianmusic.com/manifest.json` (auto-generated summary)
- **Base URL:** `https://api.cambrianmusic.com`
- **Rate limit:** 100 req/min per API key, or per IP for anonymous endpoints
- **Contract source of truth:** [`contracts/openapi.v1.json`](../contracts/openapi.v1.json) (versioned in-repo)

## The API at a glance

```
Catalogue (public, optionally keyed)
  GET  /api/v1/tracks                        Browse + filter the catalogue
  GET  /api/v1/tracks/{id}                   Single track detail
  GET  /api/v1/genres                        Distinct genres with counts
  GET  /api/v1/creators/{identifier}         Creator profile by UUID or username

Licensing (requires X-API-Key)
  POST /api/v1/licenses                      Initiate Stripe checkout
  GET  /api/v1/licenses/{id}/verify          Public license verification

API key management (requires JWT, not API key)
  POST /api/v1/keys                          Create a new key (raw key shown once)
  GET  /api/v1/keys                          List your keys (prefix only, no hashes)
  DEL  /api/v1/keys/{id}                     Revoke a key (soft delete)

MCP server (anonymous, read-only)
  POST https://api.cambrianmusic.com/mcp     Streamable HTTP MCP endpoint
         Tools:    search_tracks, get_track_details, get_track_licenses,
                   get_track_preview, get_creator_profile
         Resources: cambrian://tracks/{trackId}
                    cambrian://tracks/{trackId}/licenses
                    cambrian://creators/{creatorId}
```

## License types and usage types

When you call `POST /api/v1/licenses`, you pick one of each:

| `licenseType`        | Meaning                                                                                                       |
| -------------------- | ------------------------------------------------------------------------------------------------------------- |
| `non-exclusive`      | Default. Buyer can use the track; creator can keep selling it to others.                                      |
| `standard`           | Legacy alias for `non-exclusive`.                                                                              |
| `exclusive`          | Only the buyer can use it going forward; the listing is marked sold on Cambrian.                              |
| `copyright_buyout`   | Full copyright transfer. The creator loses the listing. Reserve for work-for-hire and in-house productions.    |

| `usageType` | Notes                                    |
| ----------- | ---------------------------------------- |
| `personal`  | Default. Private/personal use only.      |
| `youtube`   | YouTube channel content                  |
| `ads`       | Paid advertising and branded content     |
| `podcast`   | Podcast episodes and intros              |
| `game`      | In-game music and sound                  |
| `film`      | Film and long-form video                 |
| `social`    | TikTok, Instagram, Reels, short-form     |

`usageType` doesn't change the price — it's stamped on the issued `LicenseCertificate` so the buyer has an auditable record of what they bought it for.

## Need help?

- **Bugs, questions, feature requests:** open an issue on [github.com/CambrianHQ/Cambrian-Backend-Api](https://github.com/CambrianHQ/Cambrian-Backend-Api/issues).
- **Partner access / server-to-server delivery / higher rate limits:** email developers@cambrianmusic.com.
