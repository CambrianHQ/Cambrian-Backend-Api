# Add Cambrian music discovery to an AI video tool

Use Cambrian's read-only V1 API to search tracks by mood, genre, tempo, and
instrumentality:

```js
const BASE = "https://cambrian-backend-api.onrender.com";

export async function findTracks({ mood, genre, tempo }) {
  const query = new URLSearchParams({ mood, genre, tempo, limit: "10" });
  const response = await fetch(`${BASE}/api/v1/tracks?${query}`, {
    headers: { "X-API-Key": process.env.CAMBRIAN_API_KEY },
  });
  if (!response.ok) throw new Error(`Cambrian search failed: ${response.status}`);
  return response.json();
}
```

API keys do not authorize purchases or full-quality downloads. Send the user to
the authenticated Cambrian web application to complete account and payment
flows.
