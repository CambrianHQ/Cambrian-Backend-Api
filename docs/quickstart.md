# Cambrian API quickstart

**Live API:** `https://cambrian-backend-api.onrender.com`

**OpenAPI:** `https://cambrian-backend-api.onrender.com/openapi.json`

The public V1 integration surface is read-only discovery. API keys identify
integration traffic; they are not interactive user sessions and cannot access
account, billing, upload, payout, settings, or API-key-management routes.

## Create an API key

Create and manage keys from the authenticated Cambrian dashboard. The raw key is
shown once. Send it only to explicitly supported integration routes:

```http
X-API-Key: cbr_your_key
```

## Search tracks

```bash
curl "https://cambrian-backend-api.onrender.com/api/v1/tracks?limit=3" \
  -H "X-API-Key: $CAMBRIAN_API_KEY"
```

Supported filters include `search`, `genre`, `mood`, `tempo`,
`instrumental`, `sort`, `page`, and `limit`.

## Get one track

```bash
curl "https://cambrian-backend-api.onrender.com/api/v1/tracks/CAMB-TRK-A1B2C3D4" \
  -H "X-API-Key: $CAMBRIAN_API_KEY"
```

## Get a creator

```bash
curl "https://cambrian-backend-api.onrender.com/api/v1/creators/creator-slug" \
  -H "X-API-Key: $CAMBRIAN_API_KEY"
```

Purchases, downloads, uploads, profile changes, settings, billing, payouts, and
key management require an interactive Cambrian JWT/cookie session. An API key
cannot be exchanged for a JWT.

For agent integrations, see
[Build a music-aware AI agent with MCP](./tutorials/build-music-aware-mcp-agent.md).
