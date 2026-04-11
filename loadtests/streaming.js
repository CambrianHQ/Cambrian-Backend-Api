import http from "k6/http";
import { check, sleep } from "k6";

/**
 * Audio Streaming Performance Test
 *
 * Tests the /stream/{trackId}/audio endpoint which returns a 302 redirect
 * to a presigned S3/Supabase URL. Measures redirect latency (not download).
 *
 * Requires seeded track IDs (CAMB-TRK-SEED0001 through SEED0018).
 * First fetches the catalog to get real track IDs.
 *
 * Run: k6 run loadtests/streaming.js
 * Staging: k6 run -e BASE_URL=https://cambrian-api-staging-99kn.onrender.com loadtests/streaming.js
 */

export const options = {
  stages: [
    { duration: "1m", target: 20 },
    { duration: "3m", target: 80 },
    { duration: "2m", target: 150 },
    { duration: "1m", target: 0 },
  ],
  thresholds: {
    "http_req_duration{name:stream_redirect}": ["p(95)<500"],
    "http_req_duration{name:catalog_fetch}": ["p(95)<300"],
    http_req_failed: ["rate<0.02"],
  },
};

const BASE_URL = __ENV.BASE_URL || "http://localhost:5000";

// Collect track IDs from catalog on setup
let trackIds = [];

export function setup() {
  const res = http.get(`${BASE_URL}/catalog?pageSize=50`, {
    tags: { name: "catalog_fetch" },
  });

  if (res.status === 200) {
    try {
      const data = JSON.parse(res.body);
      const items = data.data?.items || [];
      trackIds = items.map((t) => t.id).filter(Boolean);
      console.log(`Collected ${trackIds.length} track IDs for streaming test`);
    } catch (_) {
      console.error("Failed to parse catalog response");
    }
  }

  return { trackIds };
}

export default function (data) {
  const ids = data.trackIds || [];
  if (ids.length === 0) {
    // Fallback: just hit catalog
    const res = http.get(`${BASE_URL}/catalog`, {
      tags: { name: "catalog_fetch" },
    });
    check(res, { "catalog fallback 200": (r) => r.status === 200 });
    sleep(1);
    return;
  }

  const trackId = ids[Math.floor(Math.random() * ids.length)];

  // Request audio stream (expect 302 redirect to presigned URL)
  // redirects: 0 — we only want to measure the API's redirect latency,
  // not the actual S3 download time.
  const res = http.get(`${BASE_URL}/stream/${trackId}/audio`, {
    redirects: 0,
    tags: { name: "stream_redirect" },
  });

  check(res, {
    "stream returns redirect or ok": (r) =>
      r.status === 302 || r.status === 200,
    "stream latency < 500ms": (r) => r.timings.duration < 500,
  });

  sleep(0.5);

  // Simulate browsing between streams
  const res2 = http.get(`${BASE_URL}/tracks/${trackId}`, {
    tags: { name: "catalog_fetch" },
  });
  check(res2, { "track detail 200": (r) => r.status === 200 });

  sleep(1);
}
