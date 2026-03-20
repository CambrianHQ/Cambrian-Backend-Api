#!/usr/bin/env node
/**
 * Patches OpenAPI v1 contract with:
 *  - GET /admin/tracks, /admin/purchases, /admin/payouts endpoints
 *  - AdminTrack, AdminPurchase, AdminPayout schemas
 *  - AdminUser updated with displayName, creatorTier, uploadCount, createdAt
 *  - AdminDashboardSummary count fields typed as integer
 */
const fs = require("fs");
const path = require("path");

const contractPath = path.resolve(__dirname, "..", "contracts", "openapi.v1.json");
const spec = JSON.parse(fs.readFileSync(contractPath, "utf8"));

// ── New paths ──────────────────────────────────────────────────

spec.paths["/admin/tracks"] = {
  get: {
    tags: ["Admin"],
    summary: "List all tracks",
    responses: {
      "200": {
        description: "OK",
        content: {
          "application/json": {
            schema: { type: "array", items: { $ref: "#/components/schemas/AdminTrack" } }
          }
        }
      }
    }
  }
};

spec.paths["/admin/purchases"] = {
  get: {
    tags: ["Admin"],
    summary: "List all purchases",
    responses: {
      "200": {
        description: "OK",
        content: {
          "application/json": {
            schema: { type: "array", items: { $ref: "#/components/schemas/AdminPurchase" } }
          }
        }
      }
    }
  }
};

spec.paths["/admin/payouts"] = {
  get: {
    tags: ["Admin"],
    summary: "List all payouts (all statuses)",
    responses: {
      "200": {
        description: "OK",
        content: {
          "application/json": {
            schema: { type: "array", items: { $ref: "#/components/schemas/AdminPayout" } }
          }
        }
      }
    }
  }
};

// ── New schemas ────────────────────────────────────────────────

spec.components.schemas["AdminTrack"] = {
  type: "object",
  properties: {
    id: { type: "string" },
    title: { type: "string" },
    genre: { type: "string", nullable: true },
    creatorId: { type: "string" },
    creatorEmail: { type: "string", nullable: true },
    status: { type: "string" },
    visibility: { type: "string" },
    nonExclusivePriceCents: { type: "integer" },
    exclusivePriceCents: { type: "integer" },
    copyrightBuyoutPriceCents: { type: "integer" },
    createdAt: { type: "string", format: "date-time" }
  }
};

spec.components.schemas["AdminPurchase"] = {
  type: "object",
  properties: {
    id: { type: "string" },
    buyerId: { type: "string" },
    buyerEmail: { type: "string", nullable: true },
    trackId: { type: "string" },
    trackTitle: { type: "string", nullable: true },
    amountCents: { type: "integer" },
    licenseType: { type: "string", nullable: true },
    status: { type: "string" },
    completedAt: { type: "string", format: "date-time", nullable: true },
    createdAt: { type: "string", format: "date-time" }
  }
};

spec.components.schemas["AdminPayout"] = {
  type: "object",
  properties: {
    id: { type: "string" },
    creatorId: { type: "string" },
    creatorEmail: { type: "string", nullable: true },
    amountCents: { type: "integer" },
    status: { type: "string", enum: ["pending", "approved", "rejected", "completed"] },
    requestedAt: { type: "string", format: "date-time" },
    completedAt: { type: "string", format: "date-time", nullable: true }
  }
};

// ── Update AdminUser schema ────────────────────────────────────

if (spec.components.schemas["AdminUser"]) {
  const props = spec.components.schemas["AdminUser"].properties || {};
  props.displayName = { type: "string", nullable: true };
  props.creatorTier = { type: "string" };
  props.uploadCount = { type: "integer" };
  props.createdAt = { type: "string", format: "date-time" };
  spec.components.schemas["AdminUser"].properties = props;
}

// ── Update AdminDashboardSummary count fields to integer ───────

if (spec.components.schemas["AdminDashboardSummary"]) {
  const props = spec.components.schemas["AdminDashboardSummary"].properties || {};
  for (const field of ["totalUsers", "activeCreators", "tracksUploaded", "licensesSold"]) {
    if (props[field]) props[field].type = "integer";
  }
}

fs.writeFileSync(contractPath, JSON.stringify(spec));
console.log("openapi.v1.json patched successfully.");
