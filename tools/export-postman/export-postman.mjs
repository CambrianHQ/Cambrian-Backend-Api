#!/usr/bin/env node

/**
 * OpenAPI -> Postman collection export script.
 *
 * Supports both the template contract path from the starter docs:
 *   - contract/cambrian-api.yaml
 * and the current repo contract path:
 *   - contracts/openapi.v1.json
 */

import fs from "fs";
import path from "path";
import yaml from "yaml";
import openapiToPostman from "openapi-to-postmanv2";

const ROOT = process.cwd();
const OPENAPI_CANDIDATES = [
  path.join(ROOT, "contract", "cambrian-api.yaml"),
  path.join(ROOT, "contract", "cambrian-api.yml"),
  path.join(ROOT, "contract", "cambrian-api.json"),
  path.join(ROOT, "contracts", "openapi.v1.json"),
  path.join(ROOT, "contracts", "openapi.v1.yaml"),
  path.join(ROOT, "contracts", "openapi.v1.yml"),
];
const OUTPUT_DIR = path.join(ROOT, "postman", "generated");
const OUTPUT_COLLECTION = path.join(
  OUTPUT_DIR,
  "Cambrian API.postman_collection.json"
);

/* ------------------------------------------------------------------ */
/*  Helpers                                                           */
/* ------------------------------------------------------------------ */

function ensureDir(dirPath) {
  fs.mkdirSync(dirPath, { recursive: true });
}

function resolveOpenApiPath() {
  const match = OPENAPI_CANDIDATES.find((candidate) => fs.existsSync(candidate));
  if (!match) {
    throw new Error(
      `OpenAPI spec not found. Checked:\n${OPENAPI_CANDIDATES.map((candidate) => `- ${candidate}`).join("\n")}`
    );
  }
  return match;
}

function readSpec(filePath) {
  const raw = fs.readFileSync(filePath, "utf8");
  if (filePath.endsWith(".yaml") || filePath.endsWith(".yml")) {
    return yaml.parse(raw);
  }
  return JSON.parse(raw);
}

/** Replace all server URLs with the {{baseUrl}} Postman variable. */
function normalizeServers(spec) {
  const clone = structuredClone(spec);
  if (!clone.servers || clone.servers.length === 0) {
    clone.servers = [{ url: "{{baseUrl}}" }];
  } else {
    clone.servers = clone.servers.map((s) => ({ ...s, url: "{{baseUrl}}" }));
  }
  return clone;
}

function addAuth(collection) {
  collection.auth = {
    type: "bearer",
    bearer: [
      { key: "token", value: "{{authToken}}", type: "string" },
    ],
  };
}

/** Inject sensible default Postman variables. */
function addCollectionVariables(collection) {
  collection.variable = [
    { key: "baseUrl", value: "https://staging-api.cambrianmusic.com" },
    { key: "authToken", value: "" },
    { key: "creatorToken", value: "" },
    { key: "adminToken", value: "" },
    { key: "trackId", value: "" },
    { key: "albumId", value: "" },
    { key: "creatorId", value: "" }
  ];
}

/** Add a minimal "no 5xx" test script to every request. */
function addDefaultTests(items = []) {
  for (const item of items) {
    if (item.item) {
      addDefaultTests(item.item);
      continue;
    }
    item.event = [
      {
        listen: "test",
        script: {
          type: "text/javascript",
          exec: [
            "pm.test('Status code is not 5xx', function () {",
            "  pm.expect(pm.response.code).to.be.below(500);",
            "});"
          ]
        }
      }
    ];
  }
}

/* ------------------------------------------------------------------ */
/*  Conversion                                                        */
/* ------------------------------------------------------------------ */

async function convertOpenApiToPostman(spec) {
  return new Promise((resolve, reject) => {
    openapiToPostman.convert(
      { type: "json", data: JSON.stringify(spec) },
      {
        folderStrategy: "tags",
        requestNameSource: "fallback",
        schemaFaker: true,
        includeAuthInfoInExample: true,
        enableOptionalParameters: true,
        optimizeConversion: true,
      },
      (err, result) => {
        if (err) return reject(err);
        if (!result.result) {
          return reject(new Error(result.reason || "Postman conversion failed."));
        }
        resolve(result.output[0].data);
      }
    );
  });
}

/* ------------------------------------------------------------------ */
/*  Main                                                              */
/* ------------------------------------------------------------------ */

async function main() {
  ensureDir(OUTPUT_DIR);

  const openApiPath = resolveOpenApiPath();
  console.log(`Reading OpenAPI spec from ${openApiPath}`);

  const spec = normalizeServers(readSpec(openApiPath));

  console.log("Converting to Postman collection…");
  const collection = await convertOpenApiToPostman(spec);

  collection.info.name = "Cambrian API";
  collection.info.description =
    "Auto-generated from the Cambrian OpenAPI contract.";

  addCollectionVariables(collection);
  addAuth(collection);
  addDefaultTests(collection.item);

  fs.writeFileSync(OUTPUT_COLLECTION, JSON.stringify(collection, null, 2));
  console.log(`✅ Postman collection generated at: ${OUTPUT_COLLECTION}`);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
