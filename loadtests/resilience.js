import http from "k6/http";
import { check, sleep } from "k6";

/**
 * Resilience / chaos-tolerant smoke test.
 *
 * This script does not inject failures by itself. Instead, it is meant to run
 * while infrastructure faults are introduced externally, such as DB failover,
 * storage disruption, cache disablement, or synthetic latency.
 *
 * Run:
 *   k6 run loadtests/resilience.js
 *   k6 run -e BASE_URL=https://staging-api.cambrianmusic.com loadtests/resilience.js
 */

export const options = {
  stages: [
    { duration: "1m", target: 50 },
    { duration: "3m", target: 200 },
    { duration: "1m", target: 0 },
  ],
  thresholds: {
    http_req_failed: ["rate<0.25"],
    http_req_duration: ["p(95)<1500"],
  },
};

const BASE_URL = __ENV.BASE_URL || "http://localhost:5000";

export default function () {
  const catalog = http.get(`${BASE_URL}/catalog`);
  check(catalog, {
    "catalog status is controlled": (r) => r.status === 200 || r.status === 503,
  });

  const discover = http.get(`${BASE_URL}/discover?genre=ambient`);
  check(discover, {
    "discover status is controlled": (r) => r.status === 200 || r.status === 503,
  });

  const health = http.get(`${BASE_URL}/health`);
  check(health, {
    "health remains reachable": (r) => r.status === 200,
  });

  sleep(1);
}
