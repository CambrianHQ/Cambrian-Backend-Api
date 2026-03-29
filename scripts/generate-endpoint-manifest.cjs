#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const openApiPath = path.join(root, "contracts", "openapi.v1.json");
const manifestPath = path.join(root, "contracts", "endpoint-manifest.v1.json");

const spec = JSON.parse(fs.readFileSync(openApiPath, "utf8"));

const publicPaths = new Set([
  "/auth/register",
  "/auth/login",
  "/auth/google",
  "/auth/google/status",
  "/auth/health",
  "/auth/csrf-token",
  "/auth/forgot-password",
  "/auth/verify-code",
  "/auth/reset-password",
  "/auth/recover-username",
  "/auth/username-availability",
  "/discover",
  "/catalog",
  "/trending",
  "/tracks/{trackId}",
  "/creator-profile/{slug}",
  "/creator-profile/{slug}/storefront",
  "/creator-profile/{slug}/collections",
  "/health",
  "/health/storage",
  "/webhook/stripe"
]);

function inferAuth(pathName, operation) {
  if (publicPaths.has(pathName)) return { requiresAuth: false };
  if (pathName.startsWith("/admin")) return { requiresAuth: true, requiresRole: "Admin" };
  if (pathName === "/settings/profile/avatar") return { requiresAuth: true };
  if (pathName.startsWith("/payouts") || pathName === "/earnings") return { requiresAuth: true, requiresRole: "Creator" };
  if (pathName === "/upload") return { requiresAuth: true, requiresRole: "Creator" };
  if (pathName === "/api/uploads/creator-image-url") return { requiresAuth: true, requiresRole: "Creator" };
  if (pathName === "/api/uploads/creator-image") return { requiresAuth: true, requiresRole: "Creator" };
  if (pathName.startsWith("/creator-profile/{slug}/follow")) return { requiresAuth: true };
  if (pathName.startsWith("/creator-profile/me") || pathName.startsWith("/settings") || pathName.startsWith("/library") || pathName.startsWith("/checkout") || pathName.startsWith("/wallet") || pathName.startsWith("/billing") || pathName.startsWith("/download") || pathName.startsWith("/licenses") || pathName.startsWith("/subscriptions") || pathName.startsWith("/payments") || pathName.startsWith("/users/me") || pathName.startsWith("/uploads/image") || pathName.startsWith("/auth/me") || pathName.startsWith("/auth/logout") || pathName.startsWith("/auth/refresh") || pathName.startsWith("/auth/set-password") || pathName.startsWith("/auth/link-google") || pathName.startsWith("/auth/set-username")) {
    return { requiresAuth: true };
  }

  const opSecurity = Array.isArray(operation.security) ? operation.security : null;
  if (opSecurity && opSecurity.length === 0) return { requiresAuth: false };
  if (opSecurity && opSecurity.some((entry) => entry.Bearer)) return { requiresAuth: true };

  return { requiresAuth: false };
}

const endpoints = [];
for (const [pathName, pathItem] of Object.entries(spec.paths || {})) {
  for (const method of ["get", "post", "put", "delete", "patch"]) {
    const operation = pathItem[method];
    if (!operation) continue;
    const auth = inferAuth(pathName, operation);
    const endpoint = {
      method: method.toUpperCase(),
      path: pathName,
      requiresAuth: !!auth.requiresAuth,
      tag: Array.isArray(operation.tags) && operation.tags.length > 0 ? operation.tags[0] : "Other"
    };
    if (auth.requiresRole) endpoint.requiresRole = auth.requiresRole;
    endpoints.push(endpoint);
  }
}

endpoints.sort((a, b) => {
  const pathSort = a.path.localeCompare(b.path);
  if (pathSort !== 0) return pathSort;
  return a.method.localeCompare(b.method);
});

const deduped = [];
const seen = new Set();
for (const endpoint of endpoints) {
  const key = `${endpoint.method} ${endpoint.path}`;
  if (seen.has(key)) continue;
  seen.add(key);
  deduped.push(endpoint);
}

fs.writeFileSync(
  manifestPath,
  JSON.stringify(
    {
      version: "v1",
      generatedAt: new Date().toISOString(),
      endpoints: deduped
    },
    null,
    2
  ) + "\n",
  "utf8"
);

console.log(`Generated contracts/endpoint-manifest.v1.json with ${deduped.length} endpoints`);
