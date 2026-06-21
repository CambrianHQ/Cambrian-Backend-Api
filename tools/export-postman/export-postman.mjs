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
import crypto from "node:crypto";
import yaml from "yaml";

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

function sampleForSchema(schema, spec, depth = 0) {
  if (!schema || depth > 5) return {};
  if (schema.example !== undefined) return schema.example;
  if (schema.default !== undefined) return schema.default;
  if (schema.$ref) {
    const target = schema.$ref
      .replace(/^#\//, "")
      .split("/")
      .reduce((value, key) => value?.[key], spec);
    return sampleForSchema(target, spec, depth + 1);
  }
  if (schema.enum?.length) return schema.enum[0];
  if (schema.type === "array") return [sampleForSchema(schema.items, spec, depth + 1)];
  if (schema.type === "object" || schema.properties) {
    return Object.fromEntries(
      Object.entries(schema.properties ?? {}).map(([key, value]) => [
        key,
        sampleForSchema(value, spec, depth + 1),
      ])
    );
  }
  if (schema.type === "boolean") return false;
  if (schema.type === "integer" || schema.type === "number") return 0;
  return "";
}

function convertOpenApiToPostman(spec) {
  const folders = new Map();
  const httpMethods = new Set([
    "get", "post", "put", "patch", "delete", "head", "options",
  ]);

  for (const [route, pathItem] of Object.entries(spec.paths ?? {})) {
    for (const [method, operation] of Object.entries(pathItem ?? {})) {
      if (!httpMethods.has(method.toLowerCase())) continue;

      const tag = operation.tags?.[0] ?? "Other";
      if (!folders.has(tag)) folders.set(tag, []);

      const parameters = [
        ...(pathItem.parameters ?? []),
        ...(operation.parameters ?? []),
      ];
      const query = parameters
        .filter((parameter) => parameter.in === "query")
        .map((parameter) => ({
          key: parameter.name,
          value: String(parameter.example ?? parameter.schema?.default ?? ""),
          disabled: !parameter.required,
        }));
      const postmanPath = route.replaceAll(/{([^}]+)}/g, ":$1");
      const rawQuery = query.length
        ? `?${query.map(({ key, value }) => `${key}=${encodeURIComponent(value)}`).join("&")}`
        : "";

      const request = {
        method: method.toUpperCase(),
        header: [],
        url: {
          raw: `{{baseUrl}}${postmanPath}${rawQuery}`,
          host: ["{{baseUrl}}"],
          path: postmanPath.split("/").filter(Boolean),
          query,
        },
        description: operation.description ?? operation.summary ?? "",
      };

      const jsonBody = operation.requestBody?.content?.["application/json"];
      if (jsonBody) {
        request.header.push({ key: "Content-Type", value: "application/json" });
        request.body = {
          mode: "raw",
          raw: JSON.stringify(sampleForSchema(jsonBody.schema, spec), null, 2),
          options: { raw: { language: "json" } },
        };
      }

      folders.get(tag).push({
        name: operation.summary ?? operation.operationId ?? `${method.toUpperCase()} ${route}`,
        request,
        response: [],
      });
    }
  }

  return {
    info: {
      _postman_id: crypto.randomUUID(),
      name: spec.info?.title ?? "Cambrian API",
      description: spec.info?.description ?? "",
      schema: "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
    },
    item: [...folders.entries()].map(([name, item]) => ({ name, item })),
  };
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
  const collection = convertOpenApiToPostman(spec);

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
