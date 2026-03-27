#!/usr/bin/env node
"use strict";
const fs = require("fs");
const path = require("path");
const CONTRACT = path.resolve(__dirname, "..", "contracts", "openapi.v1.json");
const spec = JSON.parse(fs.readFileSync(CONTRACT, "utf-8"));

// Fix routes: ensure correct HTTP methods for existing controller routes
const fixes = {
  "/generate": { post: { tags: ["AI"], summary: "Generate AI content", responses: { "200": { description: "OK" } } } },
  "/settings/profile": { get: { tags: ["Auth"], summary: "Get user profile settings", responses: { "200": { description: "OK" } } } },
  "/settings/password": {
    post: { tags: ["Auth"], summary: "Change password", responses: { "200": { description: "OK" } } },
    put: { tags: ["Auth"], summary: "Update password", responses: { "200": { description: "OK" } } }
  },
  "/settings/email": {
    post: { tags: ["Auth"], summary: "Change email", responses: { "200": { description: "OK" } } },
    put: { tags: ["Auth"], summary: "Update email", responses: { "200": { description: "OK" } } }
  },
  "/creator/username-availability": { get: { tags: ["Creators"], summary: "Check username availability", parameters: [{ name: "username", in: "query", schema: { type: "string" } }], responses: { "200": { description: "OK" } } } },
  "/api/creator/me": {
    get: { tags: ["Creators"], summary: "Get current creator profile", responses: { "200": { description: "OK" } } },
    put: { tags: ["Creators"], summary: "Update current creator profile", responses: { "200": { description: "OK" } } }
  },
  "/api/uploads/creator-image-url": { post: { tags: ["Uploads"], summary: "Upload creator image by URL", responses: { "200": { description: "OK" } } } },
  "/api/uploads/creator-image/{**key}": { put: { tags: ["Uploads"], summary: "Update creator image by key", responses: { "200": { description: "OK" } } } },
  "/api/uploads/creator-image": { post: { tags: ["Uploads"], summary: "Upload creator image", responses: { "200": { description: "OK" } } } },
  "/purchases": { post: { tags: ["Payments"], summary: "Create purchase", responses: { "200": { description: "OK" } } } },
  "/purchases/credit-creator": { post: { tags: ["Payments"], summary: "Credit creator for purchase", responses: { "200": { description: "OK" } } } },
  "/earnings": { get: { tags: ["Payouts"], summary: "Get earnings summary", responses: { "200": { description: "OK" } } } }
};

for (const [route, methods] of Object.entries(fixes)) {
  spec.paths[route] = methods;
  console.log("Fixed:", route, "→", Object.keys(methods).join(", ").toUpperCase());
}

fs.writeFileSync(CONTRACT, JSON.stringify(spec, null, 2));
console.log("Routes:", Object.keys(spec.paths).length);
