# Build a music-aware AI agent with MCP

Cambrian exposes a read-only MCP server at:

`https://cambrian-backend-api.onrender.com/mcp`

MCP requires a Cambrian API key:

```http
X-API-Key: cbr_your_key
```

Available tools:

- `search_tracks`
- `get_track_details`
- `get_track_preview`
- `get_creator_profile`

Available resources:

- `cambrian://tracks/{trackId}`
- `cambrian://creators/{creatorId}`

Example request:

```bash
curl -X POST "https://cambrian-backend-api.onrender.com/mcp" \
  -H "X-API-Key: $CAMBRIAN_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

The MCP surface is discovery-only. Account management, uploads, billing,
payouts, and purchases are not MCP tools.
