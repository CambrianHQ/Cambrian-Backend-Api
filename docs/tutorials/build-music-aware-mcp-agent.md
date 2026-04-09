# Build a music-aware AI agent with MCP

**For:** Developers building LLM agents (Claude Desktop users, Cursor users, custom agent frameworks, anything that speaks the Model Context Protocol).
**Time:** 10 minutes.
**You'll build:** An LLM session that can discover tracks, inspect license options, and reason about music selection — all through the Cambrian MCP server, no REST glue code required.

---

## Why MCP for music?

REST APIs are fine when you already know what you want. But if you're prompting an LLM with *"pick me something that matches the vibe of a late-night noir short film, under 90 seconds, no vocals"*, you don't want to hand-roll the filter translation. You want the model to call a search tool directly and reason about the results.

That's exactly what the Cambrian MCP server exposes. It's the same track discovery engine that powers the V1 REST API, just shaped as native MCP **tools** and **resources** so any MCP-aware client can use it out of the box.

---

## What the server exposes

### Tools

Called by the model with structured arguments. Each tool returns a JSON string with the result.

| Tool                  | Arguments                                                                                                                                                      | Returns                                                      |
| --------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------ |
| `search_tracks`       | `query`, `useCase`, `genre`, `mood`, `bpm`, `key`, `instrumentalOnly`, `vocalsAllowed`, `commercialUseRequired`, `minDurationSeconds`, `maxDurationSeconds`, `page`, `pageSize` | Ranked search results with relevance scores and license info |
| `get_track_details`   | `trackId` (CambrianTrackId or UUID)                                                                                                                            | Full track metadata including attributes and creator         |
| `get_track_licenses`  | `trackId`                                                                                                                                                      | All license tiers with prices, allowed uses, restrictions    |
| `get_track_preview`   | `trackId`                                                                                                                                                      | Preview audio URL, duration, format                          |
| `get_creator_profile` | `creatorId` (username or user ID)                                                                                                                              | Bio, track count, featured genres/moods, catalogue highlights |

All inputs are optional where marked; `search_tracks` will happily run on just a `query` string, or just a couple of structured filters, or both.

### Resources

Named URIs the client can subscribe to or reference directly in context. Each one returns JSON on read:

- `cambrian://tracks/{trackId}` — full track details
- `cambrian://tracks/{trackId}/licenses` — license options
- `cambrian://creators/{creatorId}` — creator profile

Resources are useful when you want to pin a specific track into the model's context without making a tool call for it (e.g. "here's the track the user already selected — reason about whether it fits the other constraints").

---

## Connect from Claude Desktop

Claude Desktop supports remote MCP servers via its `claude_desktop_config.json`. Add Cambrian to the `mcpServers` block:

```json
{
  "mcpServers": {
    "cambrian": {
      "url": "https://api.cambrianmusic.com/mcp",
      "transport": "streamable-http"
    }
  }
}
```

> **Verify the URL.** The Cambrian MCP server is mounted on the API host at the path chosen by `app.MapMcp()` in the ASP.NET Core MCP SDK. If the path above 404s, try `https://api.cambrianmusic.com/` (root) — the SDK's default may vary by version. A quick `curl -X POST https://api.cambrianmusic.com/mcp -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'` will tell you immediately: a 200 with a JSON-RPC response means you've found it.

Config file locations:

- **macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Windows:** `%APPDATA%\Claude\claude_desktop_config.json`
- **Linux:** `~/.config/Claude/claude_desktop_config.json`

Restart Claude Desktop. The Cambrian tools should now show up in the tool picker — you'll see `search_tracks`, `get_track_details`, and the rest of the family.

### Try it

Open a new Claude conversation and prompt:

> *I'm scoring a 60-second cinematic teaser — moody, dramatic, no vocals, ideally under 90 BPM. Search the Cambrian catalogue and give me three options with their license prices.*

Claude will call `search_tracks` with something like `{ mood: "dramatic", instrumentalOnly: true, maxDurationSeconds: 90, vocalsAllowed: false }`, summarize the top results, and — if you ask it — chain into `get_track_licenses` for each candidate to compare pricing.

---

## Connect from the Anthropic SDK

If you're building your own agent rather than using Claude Desktop, the [Anthropic SDK](https://docs.claude.com/en/api/messages) supports MCP servers directly via the `mcp_servers` parameter. No tool-bridging code required.

```python
# pip install anthropic
from anthropic import Anthropic

client = Anthropic()

response = client.messages.create(
    model="claude-opus-4-6",
    max_tokens=2048,
    mcp_servers=[
        {
            "type": "url",
            "url": "https://api.cambrianmusic.com/mcp",
            "name": "cambrian",
        }
    ],
    messages=[
        {
            "role": "user",
            "content": (
                "Find me three chill lofi tracks under two minutes, "
                "all instrumental, ranked by trending score. For each, "
                "quote the non-exclusive license price."
            ),
        }
    ],
)

for block in response.content:
    if block.type == "text":
        print(block.text)
```

The model will decide on its own when to call `search_tracks`, when to chase a specific track with `get_track_details` or `get_track_licenses`, and when it has enough to answer. You just get the synthesized response back.

### TypeScript equivalent

```ts
// npm install @anthropic-ai/sdk
import Anthropic from "@anthropic-ai/sdk";

const client = new Anthropic();

const response = await client.messages.create({
  model: "claude-opus-4-6",
  max_tokens: 2048,
  mcp_servers: [
    {
      type: "url",
      url: "https://api.cambrianmusic.com/mcp",
      name: "cambrian",
    },
  ],
  messages: [
    {
      role: "user",
      content:
        "Find three chill lofi tracks under two minutes. For each, show the non-exclusive price.",
    },
  ],
});

for (const block of response.content) {
  if (block.type === "text") console.log(block.text);
}
```

---

## Example agent flows

### "Score my video"

```
User:   I have a 45-second travel reel set in Tokyo at night.
        Find 3 tracks that would work, all instrumental, and
        tell me which one has the cheapest commercial license.

Claude: [calls search_tracks with
          { query: "tokyo night travel", mood: "dreamy",
            instrumentalOnly: true, maxDurationSeconds: 60 }]
        [calls get_track_licenses for each of the top 3]
        [synthesizes comparison]

        Here are three options sorted by non-exclusive price:
        1. "Neon Underpass" by Mira Sato — $29 non-exclusive, dreamy
           synthwave, 0:52, 95 BPM. Best fit for the moody vibe.
        2. "Shibuya After Midnight" by PXLR — $35 non-exclusive,
           ambient electronic, 0:58, 80 BPM.
        3. "Rainlight" by Oboro — $45 non-exclusive, minimal piano +
           pads, 0:44, 72 BPM. Cinematic — works if you want quieter.
```

### "Build me a playlist and check rights"

```
User:   Build a 20-minute podcast intro playlist from Cambrian.
        I need 10 tracks, 1-2 min each, uplifting, with commercial
        license pricing summarized at the end.

Claude: [calls search_tracks multiple times with different filters]
        [calls get_track_licenses on each match]
        [presents sorted list with total cost to license all 10]
```

### "Research a creator"

```
User:   Tell me about the creator "neonhalo" on Cambrian — genres,
        track count, and whether they allow commercial use.

Claude: [calls get_creator_profile with creatorId: "neonhalo"]
        [summarizes bio, featured genres, and cross-references their
         catalogue's commercial-use terms]
```

---

## What's NOT exposed via MCP (yet)

The MCP server is **read-only**. It's designed for discovery, inspection, and reasoning — not state changes. That means:

- **Licensing is not an MCP tool.** You still initiate a purchase via `POST /api/v1/licenses` on the REST API. See the [programmatic licensing tutorial](./license-music-programmatically.md).
- **Key management is not an MCP tool.** Creating, listing, or revoking API keys always goes through the authenticated REST endpoints.
- **Uploads, library edits, payouts** — all REST-only.

This split is intentional: making a purchase is a side-effect with real money attached, and we don't want a hallucinated tool call moving dollars. The MCP surface is everything you'd want an agent to reason over; the REST surface is where you commit.

---

## Authentication

The MCP endpoint itself is anonymous — no API key needed. The rationale is that everything it exposes is already publicly readable via the REST catalogue, so there's no new permission boundary to enforce.

That said, anonymous requests share the global rate limit (100 req/min per IP). If you're running a multi-user agent in production, you may want to front the MCP endpoint with your own proxy that attaches a Cambrian API key, so you get per-key rate limiting instead.

---

## Recap

- Point your MCP client at `https://api.cambrianmusic.com/mcp` (verify path with a `tools/list` probe).
- Five tools for track search, details, license options, previews, and creator profiles.
- Three resources under `cambrian://` for direct in-context reference.
- Works with Claude Desktop, Cursor, the Anthropic SDK's `mcp_servers` parameter, and any compliant MCP client.
- Read-only by design — use [`POST /api/v1/licenses`](./license-music-programmatically.md) when you're ready to actually buy.

### Where to go next

- **[Add licensed music to AI-generated video](./add-licensed-music-to-ai-video.md)** — complementary flow that uses REST for licensing and MCP for discovery.
- **[License music programmatically in your content creation tool](./license-music-programmatically.md)** — the full V1 API walkthrough.
- **[5-minute quickstart](../quickstart.md)** — the REST equivalent if MCP is overkill for your use case.
