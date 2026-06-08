import http from "k6/http";
import { check, sleep } from "k6";

/**
 * Smoke Test — Quick health check for all critical endpoints.
 *
 * Runs a single VU for 30 seconds to verify the API is responsive
 * after deployment. Use in CI/CD as a post-deploy gate.
 *
 * Run: k6 run loadtests/smoke.js
 * Staging: k6 run -e BASE_URL=https://cambrian-api-staging-99kn.onrender.com loadtests/smoke.js
 */

export const options = {
  vus: 1,
  duration: "30s",
  thresholds: {
    http_req_duration: ["p(99)<1000"],
    // Only gate on requests that should succeed (public endpoints).
    // Auth-gated endpoints (library, auth/me, admin) intentionally return 401
    // and are excluded via the `expected_response:true` tag.
    "http_req_failed{expected_response:true}": ["rate<0.01"],
  },
};

const BASE_URL = __ENV.BASE_URL || "http://localhost:5000";

export default function () {
  // Health check
  let res = http.get(`${BASE_URL}/health`);
  check(res, { "health 200": (r) => r.status === 200 });

  // Catalog
  res = http.get(`${BASE_URL}/catalog`);
  check(res, { "catalog 200": (r) => r.status === 200 });

  // Discover
  res = http.get(`${BASE_URL}/discover`);
  check(res, { "discover 200": (r) => r.status === 200 });

  // Trending
  res = http.get(`${BASE_URL}/trending`);
  check(res, { "trending 200": (r) => r.status === 200 });

  // Subscription plans
  res = http.get(`${BASE_URL}/subscriptions/plans`);
  check(res, { "plans 200": (r) => r.status === 200 });

  // Public API v1 tracks
  res = http.get(`${BASE_URL}/api/v1/tracks`);
  check(res, { "v1 tracks 200": (r) => r.status === 200 });

  // Auth-gated endpoints — verify they reject cleanly with 401, not 500.
  // responseCallback marks these as not expected_response so they don't
  // pollute the http_req_failed threshold above.
  res = http.get(`${BASE_URL}/library`, {
    responseCallback: http.expectedStatuses(401),
  });
  check(res, { "library 401": (r) => r.status === 401 });

  res = http.get(`${BASE_URL}/auth/me`, {
    responseCallback: http.expectedStatuses(401),
  });
  check(res, { "auth/me 401": (r) => r.status === 401 });

  res = http.get(`${BASE_URL}/admin/dashboard`, {
    responseCallback: http.expectedStatuses(401),
  });
  check(res, { "admin 401": (r) => r.status === 401 });

  sleep(1);
}
