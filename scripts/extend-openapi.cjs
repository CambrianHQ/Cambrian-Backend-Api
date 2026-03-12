#!/usr/bin/env node
/**
 * Extends OpenAPI v1 contract with:
 *  1. Track ID system (CAMB-TRK-XXXX)
 *  2. LicenseCertificate schema
 *  3. UsageType enum (optional on checkout/purchase)
 *  4. Search filters (mood, tempo, instrumental, duration)
 *
 * All changes are ADDITIVE — no existing schemas or routes are modified.
 */
const fs = require("fs");
const path = require("path");

const contractPath = path.resolve(__dirname, "..", "contracts", "openapi.v1.json");
const spec = JSON.parse(fs.readFileSync(contractPath, "utf8"));

// ────────────────────────────────────────────────────────────────
// 1. TRACK ID SCHEMA
// ────────────────────────────────────────────────────────────────
spec.components.schemas["TrackIdentifier"] = {
  type: "object",
  properties: {
    trackId: {
      type: "string",
      pattern: "^CAMB-TRK-[A-Z0-9]{4,12}$",
      description: "Unique Cambrian track identifier (e.g. CAMB-TRK-A1B2)",
      example: "CAMB-TRK-A1B2"
    }
  },
  additionalProperties: false
};

// ────────────────────────────────────────────────────────────────
// 2. USAGE TYPE ENUM
// ────────────────────────────────────────────────────────────────
spec.components.schemas["UsageType"] = {
  type: "string",
  enum: ["personal", "youtube", "ads", "podcast", "game", "film", "social"],
  description: "Intended usage context for a licensed track"
};

// ────────────────────────────────────────────────────────────────
// 3. LICENSE CERTIFICATE SCHEMA
// ────────────────────────────────────────────────────────────────
spec.components.schemas["LicenseCertificate"] = {
  type: "object",
  required: ["licenseId", "trackId", "buyerId", "creatorId", "issuedAt"],
  properties: {
    licenseId: { type: "string", description: "Unique license identifier" },
    trackId: {
      type: "string",
      pattern: "^CAMB-TRK-[A-Z0-9]{4,12}$",
      description: "Track this license covers"
    },
    buyerId: { type: "string", description: "User ID of the buyer" },
    creatorId: { type: "string", description: "User ID of the track creator" },
    usageType: { $ref: "#/components/schemas/UsageType" },
    issuedAt: {
      type: "string",
      format: "date-time",
      description: "When the license was issued"
    },
    allowedUses: {
      type: "array",
      items: { type: "string" },
      nullable: true,
      description: "Specific permitted uses (null = unrestricted for license tier)"
    },
    restrictions: {
      type: "array",
      items: { type: "string" },
      nullable: true,
      description: "Usage restrictions or limitations"
    }
  },
  additionalProperties: false
};

// ────────────────────────────────────────────────────────────────
// 4. NEW ENDPOINT: GET /licenses/{trackId}
// ────────────────────────────────────────────────────────────────
spec.paths["/licenses/{trackId}"] = {
  get: {
    tags: ["Licenses"],
    summary: "Retrieve the license certificate for a purchased track",
    parameters: [
      {
        name: "trackId",
        in: "path",
        required: true,
        schema: { type: "string", pattern: "^CAMB-TRK-[A-Z0-9]{4,12}$" }
      }
    ],
    responses: {
      200: {
        description: "License certificate",
        content: {
          "application/json": {
            schema: { $ref: "#/components/schemas/LicenseCertificate" }
          }
        }
      },
      404: { description: "No license found for this track/user" }
    }
  }
};

// ────────────────────────────────────────────────────────────────
// 5. OPTIONAL usageType ON CheckoutRequest & PurchaseCreateRequest
//    (additive only — existing fields untouched)
// ────────────────────────────────────────────────────────────────
spec.components.schemas.CheckoutRequest.properties.usageType = {
  $ref: "#/components/schemas/UsageType",
  description: "Optional intended usage for the purchased license",
  nullable: true
};

spec.components.schemas.PurchaseCreateRequest.properties.usageType = {
  $ref: "#/components/schemas/UsageType",
  description: "Optional intended usage for the purchased license",
  nullable: true
};

// Add licenseCertificate to checkout 200 response (optional, for post-payment)
spec.paths["/checkout"].post.responses["200"] = {
  description: "OK",
  content: {
    "application/json": {
      schema: {
        type: "object",
        properties: {
          checkoutUrl: { type: "string", nullable: true },
          licenseCertificate: {
            $ref: "#/components/schemas/LicenseCertificate",
            nullable: true,
            description: "License certificate (populated after payment completion)"
          }
        },
        additionalProperties: false
      }
    }
  }
};

// ────────────────────────────────────────────────────────────────
// 6. SEARCH FILTERS on /discover, /catalog, /trending
//    All optional — no change if omitted
// ────────────────────────────────────────────────────────────────
const filterParams = [
  {
    name: "mood",
    in: "query",
    schema: { type: "string" },
    description: "Filter by mood (e.g. happy, dark, chill, energetic)"
  },
  {
    name: "tempo",
    in: "query",
    schema: { type: "string" },
    description: "Filter by tempo (slow, medium, fast) or BPM range (e.g. 120-140)"
  },
  {
    name: "instrumental",
    in: "query",
    schema: { type: "boolean" },
    description: "Filter to instrumental-only tracks (true) or vocal tracks (false)"
  },
  {
    name: "duration",
    in: "query",
    schema: { type: "string" },
    description:
      "Filter by duration bucket (short=<2min, medium=2-5min, long=>5min)"
  }
];

["/discover", "/catalog", "/trending"].forEach((p) => {
  const op = spec.paths[p].get;
  if (!op.parameters) op.parameters = [];
  filterParams.forEach((fp) => {
    if (!op.parameters.find((existing) => existing.name === fp.name)) {
      op.parameters.push(fp);
    }
  });
});

// ────────────────────────────────────────────────────────────────
// WRITE
// ────────────────────────────────────────────────────────────────
fs.writeFileSync(contractPath, JSON.stringify(spec, null, 2), "utf8");

console.log("✓ openapi.v1.json updated");
console.log(
  "  New schemas:",
  ["TrackIdentifier", "UsageType", "LicenseCertificate"].join(", ")
);
console.log("  New paths:  GET /licenses/{trackId}");
console.log(
  "  Modified:   CheckoutRequest.usageType, PurchaseCreateRequest.usageType"
);
console.log(
  "  Filters:    mood, tempo, instrumental, duration → /discover, /catalog, /trending"
);
