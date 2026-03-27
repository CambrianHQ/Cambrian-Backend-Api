#!/usr/bin/env node
"use strict";
const fs = require("fs");
const path = require("path");
const CONTRACT = path.resolve(__dirname, "..", "contracts", "openapi.v1.json");
const spec = JSON.parse(fs.readFileSync(CONTRACT, "utf-8"));

// 1. Add /activity/new
spec.paths["/activity/new"] = {
  get: {
    tags: ["Activity"],
    summary: "Get new track activity feed",
    responses: {
      "200": {
        description: "OK",
        content: { "application/json": { schema: { type: "array", items: { "$ref": "#/components/schemas/ActivityItemResponse" } } } }
      }
    }
  }
};

// 2. Add /activity/sales
spec.paths["/activity/sales"] = {
  get: {
    tags: ["Activity"],
    summary: "Get sales activity feed",
    responses: {
      "200": {
        description: "OK",
        content: { "application/json": { schema: { type: "array", items: { "$ref": "#/components/schemas/ActivityItemResponse" } } } }
      }
    }
  }
};

// 3. Add /activity/trending
spec.paths["/activity/trending"] = {
  get: {
    tags: ["Activity"],
    summary: "Get trending tracks",
    responses: {
      "200": {
        description: "OK",
        content: { "application/json": { schema: { type: "array", items: { "$ref": "#/components/schemas/TrendingTrackResponse" } } } }
      }
    }
  }
};

// 4. Add /tracks/trending
spec.paths["/tracks/trending"] = {
  get: {
    tags: ["Catalog"],
    summary: "Get trending tracks with optional limit",
    parameters: [
      { name: "limit", in: "query", schema: { type: "integer", default: 12 } }
    ],
    responses: {
      "200": {
        description: "OK",
        content: {
          "application/json": {
            schema: {
              type: "object",
              properties: {
                success: { type: "boolean" },
                data: { type: "array", items: { "$ref": "#/components/schemas/TrendingTrackResponse" } }
              }
            }
          }
        }
      }
    }
  }
};

// 5. Add POST to /analytics/events (already has GET)
if (spec.paths["/analytics/events"]) {
  spec.paths["/analytics/events"].post = {
    tags: ["Analytics"],
    summary: "Record a contract-backed analytics event",
    requestBody: {
      content: {
        "application/json": {
          schema: { "$ref": "#/components/schemas/AnalyticsEventRequest" }
        }
      }
    },
    responses: {
      "202": { description: "Accepted" },
      "400": { description: "Bad Request" }
    }
  };
}

// 6. Rename /creator-profile/me/banner -> /creator-profile/me/cover-image-upload
if (spec.paths["/creator-profile/me/banner"]) {
  spec.paths["/creator-profile/me/cover-image-upload"] = spec.paths["/creator-profile/me/banner"];
  delete spec.paths["/creator-profile/me/banner"];
}

// 7. Rename /creator-profile/me/avatar -> /creator-profile/me/profile-image-upload
if (spec.paths["/creator-profile/me/avatar"]) {
  spec.paths["/creator-profile/me/profile-image-upload"] = spec.paths["/creator-profile/me/avatar"];
  delete spec.paths["/creator-profile/me/avatar"];
}

// 8. Add schemas if missing
if (!spec.components) spec.components = {};
if (!spec.components.schemas) spec.components.schemas = {};

if (!spec.components.schemas.ActivityItemResponse) {
  spec.components.schemas.ActivityItemResponse = {
    type: "object",
    properties: {
      type: { type: "string" },
      createdAt: { type: "string", format: "date-time" },
      trackId: { type: "string", format: "uuid", nullable: true },
      trackTitle: { type: "string", nullable: true }
    }
  };
}

if (!spec.components.schemas.TrendingTrackResponse) {
  spec.components.schemas.TrendingTrackResponse = {
    type: "object",
    properties: {
      trackId: { type: "string", format: "uuid" },
      title: { type: "string" },
      score: { type: "number" },
      useCase: { type: "string", nullable: true }
    }
  };
}

if (!spec.components.schemas.AnalyticsEventRequest) {
  spec.components.schemas.AnalyticsEventRequest = {
    type: "object",
    properties: {
      type: { type: "string" },
      trackId: { type: "string", format: "uuid", nullable: true },
      metadataJson: { type: "string", nullable: true }
    }
  };
}

fs.writeFileSync(CONTRACT, JSON.stringify(spec, null, 2));
console.log("OpenAPI spec updated. Routes:", Object.keys(spec.paths).length);
