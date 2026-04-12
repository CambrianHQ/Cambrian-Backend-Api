# Resend AI Onboarding

This backend already supports Resend for transactional email. Use this guide when you want to:

- switch Cambrian from console email to real delivery with Resend
- give an AI agent current Resend docs and tools
- test delivery locally from the terminal
- point Resend webhook events back at Cambrian

## Backend config

Set these environment variables for this API:

```env
Email__Provider=resend
Email__FromAddress=noreply@yourdomain.com
Email__FromName=Cambrian Music
Email__ResendApiKey=re_xxxxxxxxx
```

The app now validates those settings at startup when `Email__Provider=resend`, so bad config fails fast instead of surfacing later on the first send attempt.

You will also need a verified sending domain in Resend. A human still needs to complete the DNS portion of that setup.

## Webhook endpoint

Cambrian exposes a Resend webhook endpoint at:

```text
POST /webhook/email
```

Current behavior:

- accepts Resend webhook payloads
- logs received event metadata
- logs inbound email details for `email.received`

If you are wiring Resend webhooks in a dashboard, point them at:

```text
https://<your-api-host>/webhook/email
```

## MCP server for agents

If your MCP client supports command-based servers, add Resend like this:

```json
{
  "mcpServers": {
    "resend": {
      "command": "npx",
      "args": ["-y", "resend-mcp"],
      "env": {
        "RESEND_API_KEY": "re_xxxxxxxxx"
      }
    }
  }
}
```

That gives the agent direct access to Resend operations without baking the API key into prompts.

For this repo specifically, you can point your MCP client at the checked-in launcher script instead of calling `npx` directly:

```json
{
  "mcpServers": {
    "resend": {
      "command": "powershell",
      "args": [
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        "ABSOLUTE_PATH_TO_REPO\\scripts\\start-resend-mcp.ps1"
      ],
      "env": {
        "RESEND_API_KEY": "re_xxxxxxxxx",
        "SENDER_EMAIL_ADDRESS": "noreply@yourdomain.com"
      }
    }
  }
}
```

Helpful repo-local shortcuts:

- `npm run start:resend-mcp`
- `powershell -ExecutionPolicy Bypass -File scripts/print-resend-mcp-config.ps1`

The print script generates a ready-to-paste Windows MCP config snippet with the absolute repo path filled in.

## CLI

For terminal-driven testing:

```bash
npm install -g resend-cli
resend login

resend emails send \
  --from "you@yourdomain.com" \
  --to hello@example.com \
  --subject "Hello" \
  --text "Sent from my terminal."
```

This is useful for confirming that your domain and API key work before you debug app-side email flows.

On Windows, Resend also publishes a PowerShell installer:

```powershell
irm https://resend.com/install.ps1 | iex
```

## Docs for agents

Resend exposes agent-friendly docs in a few formats:

- Markdown page format: append `.md` to a docs page
- Full docs bundle: `https://resend.com/docs/llms-full.txt`
- MCP docs server: `npx add-mcp https://resend.com/docs/mcp`

Those are good options when an agent needs current API details without scraping the whole site manually.

## Recommended local flow

1. Verify your sending domain in the Resend dashboard.
2. Set the four `Email__...` variables above.
3. Start the Cambrian API.
4. Trigger a real email flow such as signup verification or password reset.
5. If needed, test delivery separately with `resend emails send`.
6. Add the webhook URL in Resend if you want event or inbound-email handling.
