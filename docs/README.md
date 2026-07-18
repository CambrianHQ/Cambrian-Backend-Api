# Cambrian developer docs

The live backend is:

`https://cambrian-backend-api.onrender.com`

## Reference

- [Quickstart](./quickstart.md)
- [Interactive explorer](./api-explorer.html)
- [MCP integration](./tutorials/build-music-aware-mcp-agent.md)
- [Staging contract sync follow-up](./staging-contract-sync-follow-up.md)
- [Production audio playback contract](./PRODUCTION_AUDIO_PLAYBACK_CONTRACT.md)
- [OpenAPI](https://cambrian-backend-api.onrender.com/openapi.json)
- [Endpoint manifest](https://cambrian-backend-api.onrender.com/manifest.json)

## Public integration surface

```text
GET /api/v1/tracks
GET /api/v1/tracks/search
GET /api/v1/tracks/{id}
GET /api/v1/genres
GET /api/v1/creators/{identifier}
POST /mcp
```

The REST discovery routes may be anonymous or use `X-API-Key`. MCP requires
`X-API-Key`. API keys are route-limited integration credentials and cannot
authenticate account, billing, settings, profile mutation, upload, payout, or
API-key-management endpoints.

All account and money-changing operations require an interactive JWT/cookie
session and are intentionally outside the public integration contract.
