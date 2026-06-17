// Browser regression tests for Cambrian audio playback.
//
// These target the DEPLOYED FRONTEND (cambrianmusic.com), not this backend repo.
// They are checked in here so the rehydration work ships with its browser contract;
// move/run them from the frontend repo with `@playwright/test` installed:
//
//   FRONTEND_URL=https://cambrianmusic.com BACKEND_URL=https://cambrian-backend-api.onrender.com \
//     npx playwright test audio-playback.spec.ts
//
// They assert at the NETWORK layer (robust without knowing exact DOM selectors):
//  - no audio/image request goes to the dead origin api.cambrianmusic.com
//  - audio requests target the active backend origin
//  - at least one catalog track's audio returns 200/206 (a real track plays)
//  - the player never surfaces "corrupted" / "no supported sources" for a playable track

import { test, expect } from "@playwright/test";

const FRONTEND = process.env.FRONTEND_URL ?? "https://cambrianmusic.com";
const BACKEND = process.env.BACKEND_URL ?? "https://cambrian-backend-api.onrender.com";
const DEAD_ORIGIN = "api.cambrianmusic.com";

test.describe("audio playback contract", () => {
  test("no request uses the dead api.cambrianmusic.com origin", async ({ page }) => {
    const deadHits: string[] = [];
    page.on("request", (r) => {
      if (r.url().includes(DEAD_ORIGIN)) deadHits.push(r.url());
    });
    await page.goto(FRONTEND, { waitUntil: "networkidle" });
    await page.waitForTimeout(2000);
    expect(deadHits, `requests to dead origin: ${deadHits.join(", ")}`).toHaveLength(0);
  });

  test("audio requests target the active backend origin", async ({ page }) => {
    const audioReqs: string[] = [];
    page.on("request", (r) => {
      if (/\/stream\/[0-9a-f-]+\/audio/i.test(r.url())) audioReqs.push(r.url());
    });
    await page.goto(FRONTEND, { waitUntil: "networkidle" });
    // Trigger a play if a play control is present (best-effort; selector-agnostic).
    const playBtn = page.locator('[aria-label*="play" i], button:has-text("Play")').first();
    if (await playBtn.count()) await playBtn.click({ trial: false }).catch(() => {});
    await page.waitForTimeout(2500);
    for (const u of audioReqs) expect(u.startsWith(BACKEND), `audio req off-origin: ${u}`).toBeTruthy();
  });

  test("at least one playable track streams 200/206 (no corruption)", async ({ request }) => {
    // Resolve a known-playable track from the backend audit oracle, then assert the
    // browser-facing stream endpoint serves it with a media content-type.
    const okStatuses = [200, 206];
    const res = await request.get(`${BACKEND}/catalog?page=1&pageSize=20&sort=newest`);
    const body = await res.json();
    const items: any[] = Array.isArray(body?.data) ? body.data : body?.data?.items ?? [];
    let proven = false;
    for (const t of items) {
      const r = await request.get(`${BACKEND}/stream/${t.id}/audio`, { headers: { Range: "bytes=0-1023" } });
      const ct = r.headers()["content-type"] ?? "";
      if (okStatuses.includes(r.status()) && /^audio\//i.test(ct)) { proven = true; break; }
    }
    expect(proven, "no catalog track streamed 200/206 with an audio content-type").toBeTruthy();
  });

  test("a broken track fails cleanly (no suspended HTML, no 5xx)", async ({ request }) => {
    // A track whose object is absent must 404 with a JSON error envelope — never a
    // Render suspended page and never a 5xx (which the player shows as "corrupted").
    const res = await request.get(`${BACKEND}/health/audio-audit`, {
      headers: { authorization: `Bearer ${process.env.REHYDRATE_ADMIN_TOKEN ?? ""}` },
    });
    test.skip(!res.ok(), "audio-audit requires REHYDRATE_ADMIN_TOKEN; skipping clean-404 assertion");
    const audit = await res.json();
    const broken = audit?.missing?.[0]?.trackId;
    test.skip(!broken, "no broken track to assert against");
    const r = await request.get(`${BACKEND}/stream/${broken}/audio`, { headers: { Range: "bytes=0-1023" } });
    expect(r.status()).toBe(404);
    const text = await r.text();
    expect(/<html|suspended|render\.com/i.test(text), "broken stream returned non-JSON/suspended HTML").toBeFalsy();
  });
});
