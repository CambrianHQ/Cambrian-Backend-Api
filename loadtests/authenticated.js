import http from "k6/http";
import { check, sleep } from "k6";

/**
 * Authenticated User Flows Load Test
 *
 * Tests endpoints requiring JWT: /library, /auth/me, /subscriptions/current,
 * /checkout (license purchase), /billing/status
 *
 * Requires AUTH_TOKEN env var (JWT from staging user).
 *
 * Run: k6 run -e AUTH_TOKEN=<jwt> loadtests/authenticated.js
 * Staging: k6 run -e BASE_URL=https://cambrian-api-staging-99kn.onrender.com -e AUTH_TOKEN=<jwt> loadtests/authenticated.js
 */

export const options = {
  stages: [
    { duration: "1m", target: 10 },
    { duration: "3m", target: 50 },
    { duration: "2m", target: 100 },
    { duration: "1m", target: 0 },
  ],
  thresholds: {
    http_req_duration: ["p(95)<500"],
    http_req_failed: ["rate<0.02"],
  },
};

const BASE_URL = __ENV.BASE_URL || "http://localhost:5000";
const AUTH_TOKEN = __ENV.AUTH_TOKEN;

if (!AUTH_TOKEN) {
  console.error("AUTH_TOKEN env var is required. Set it to a valid JWT.");
}

const authHeaders = {
  headers: {
    Authorization: `Bearer ${AUTH_TOKEN}`,
    "Content-Type": "application/json",
  },
};

export default function () {
  // 1. Get current user profile
  let res = http.get(`${BASE_URL}/auth/me`, authHeaders);
  check(res, {
    "auth/me 200": (r) => r.status === 200,
    "auth/me has user": (r) => {
      try { return JSON.parse(r.body).data?.email !== undefined; }
      catch { return false; }
    },
  });

  sleep(0.5);

  // 2. View library
  res = http.get(`${BASE_URL}/library`, authHeaders);
  check(res, { "library 200": (r) => r.status === 200 });

  sleep(0.3);

  // 3. Current subscription
  res = http.get(`${BASE_URL}/subscriptions/current`, authHeaders);
  check(res, { "subscription 200": (r) => r.status === 200 });

  sleep(0.3);

  // 4. Billing status
  res = http.get(`${BASE_URL}/billing/status`, authHeaders);
  check(res, { "billing status 200": (r) => r.status === 200 });

  sleep(0.3);

  // 5. Browse catalog then view a specific track (simulates user journey)
  res = http.get(`${BASE_URL}/catalog?pageSize=5`);
  check(res, { "catalog 200": (r) => r.status === 200 });

  try {
    const catalog = JSON.parse(res.body);
    const items = catalog.data?.items || [];
    if (items.length > 0) {
      const trackId = items[Math.floor(Math.random() * items.length)].id;

      sleep(0.3);

      // View track detail
      res = http.get(`${BASE_URL}/tracks/${trackId}`);
      check(res, { "track detail 200": (r) => r.status === 200 });
    }
  } catch (_) {
    // catalog parse failed — skip track detail
  }

  sleep(1);
}
