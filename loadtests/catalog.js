import http from "k6/http";
import { check, sleep } from "k6";

/**
 * Catalog & Discovery Load Test
 *
 * Tests public endpoints: /catalog, /discover, /tracks/{id}, /trending
 * No authentication required.
 *
 * Run: k6 run loadtests/catalog.js
 * Staging: k6 run -e BASE_URL=https://cambrian-api-staging-99kn.onrender.com loadtests/catalog.js
 */

export const options = {
  stages: [
    { duration: "1m", target: 25 },   // ramp up
    { duration: "3m", target: 100 },   // sustained load
    { duration: "2m", target: 200 },   // peak load
    { duration: "1m", target: 0 },     // ramp down
  ],
  thresholds: {
    http_req_duration: ["p(95)<300"],   // 95% of requests under 300ms
    http_req_failed: ["rate<0.01"],     // <1% error rate
  },
};

const BASE_URL = __ENV.BASE_URL || "http://localhost:5000";

const GENRES = ["hip-hop", "electronic", "cinematic", "lo-fi", "orchestral", "ambient"];
const MOODS = ["chill", "energetic", "dark", "happy"];
const SEARCH_TERMS = ["beat", "fire", "loop", "chill", "dark", "epic"];

function randomItem(arr) {
  return arr[Math.floor(Math.random() * arr.length)];
}

export default function () {
  // 1. Browse catalog (paginated)
  const page = Math.floor(Math.random() * 3) + 1;
  let res = http.get(`${BASE_URL}/catalog?page=${page}&pageSize=20`);
  check(res, {
    "catalog 200": (r) => r.status === 200,
    "catalog has data": (r) => {
      try { return JSON.parse(r.body).success === true; }
      catch { return false; }
    },
  });

  sleep(0.5);

  // 2. Discover with genre filter
  const genre = randomItem(GENRES);
  res = http.get(`${BASE_URL}/discover?genre=${genre}`);
  check(res, { "discover 200": (r) => r.status === 200 });

  sleep(0.3);

  // 3. Search
  const term = randomItem(SEARCH_TERMS);
  res = http.get(`${BASE_URL}/catalog?search=${term}`);
  check(res, { "search 200": (r) => r.status === 200 });

  sleep(0.3);

  // 4. Discover with mood + genre
  const mood = randomItem(MOODS);
  res = http.get(`${BASE_URL}/discover?genre=${genre}&mood=${mood}`);
  check(res, { "filtered discover 200": (r) => r.status === 200 });

  sleep(0.3);

  // 5. Trending
  res = http.get(`${BASE_URL}/trending`);
  check(res, { "trending 200": (r) => r.status === 200 });

  sleep(0.5);

  // 6. Subscription plans (public)
  res = http.get(`${BASE_URL}/subscriptions/plans`);
  check(res, { "plans 200": (r) => r.status === 200 });

  sleep(0.5);
}
