#!/usr/bin/env node
// ──────────────────────────────────────────────────────────
// Cambrian Backend — Breaking Change Detection
// Compares a newly-generated OpenAPI spec against the
// checked-in contract baseline (contracts/openapi.v1.json).
//
// Detects:
//   1. Removed endpoints (path + method gone)
//   2. Removed required request/response fields
//   3. Changed field types
//   4. Narrowed enum values
//
// Usage:
//   node scripts/detect-breaking-changes.cjs [generated.json]
//
// If no argument is given, falls back to contracts/openapi.generated.json
// (produced by validate-openapi.ps1).
// ──────────────────────────────────────────────────────────

"use strict";

const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const BASELINE_PATH = path.join(ROOT, "contracts", "openapi.v1.json");
const DEFAULT_GENERATED = path.join(ROOT, "contracts", "openapi.generated.json");

const generatedPath = process.argv[2]
  ? path.resolve(process.argv[2])
  : DEFAULT_GENERATED;

// ── Helpers ───────────────────────────────────────────────

function loadJson(filePath) {
  if (!fs.existsSync(filePath)) {
    console.error(`File not found: ${filePath}`);
    process.exit(2);
  }
  return JSON.parse(fs.readFileSync(filePath, "utf-8"));
}

function resolveRef(spec, ref) {
  if (!ref || !ref.startsWith("#/")) return null;
  const parts = ref.replace("#/", "").split("/");
  let current = spec;
  for (const p of parts) {
    current = current?.[p];
    if (!current) return null;
  }
  return current;
}

function getSchemaProperties(spec, schema) {
  if (!schema) return {};
  if (schema.$ref) {
    schema = resolveRef(spec, schema.$ref) || {};
  }
  return schema.properties || {};
}

function getRequiredFields(spec, schema) {
  if (!schema) return [];
  if (schema.$ref) {
    schema = resolveRef(spec, schema.$ref) || {};
  }
  return schema.required || [];
}

function getResponseSchema(spec, operation) {
  const resp200 = operation?.responses?.["200"];
  if (!resp200) return null;
  const content = resp200.content;
  if (!content) return null;
  const json = content["application/json"] || content["text/json"];
  return json?.schema || null;
}

function getRequestSchema(spec, operation) {
  const body = operation?.requestBody;
  if (!body) return null;
  const content = body.content;
  if (!content) return null;
  const json = content["application/json"] || content["text/json"];
  return json?.schema || null;
}

// ── Load specs ────────────────────────────────────────────

console.log("╔══════════════════════════════════════════════╗");
console.log("║  Cambrian — Breaking Change Detection        ║");
console.log("╚══════════════════════════════════════════════╝\n");

const baseline = loadJson(BASELINE_PATH);
const generated = loadJson(generatedPath);

const breaks = [];

function breaking(category, detail) {
  breaks.push({ category, detail });
}

// ── 1. Removed endpoints ──────────────────────────────────

const METHODS = ["get", "head", "post", "put", "delete", "patch"];

for (const [pathKey, pathItem] of Object.entries(baseline.paths || {})) {
  for (const method of METHODS) {
    if (!pathItem[method]) continue;

    const genPath = generated.paths?.[pathKey];
    if (!genPath || !genPath[method]) {
      breaking(
        "endpoint-removed",
        `${method.toUpperCase()} ${pathKey} — exists in baseline but missing in new spec`
      );
    }
  }
}

// ── 2. Removed required response fields ───────────────────

for (const [pathKey, pathItem] of Object.entries(baseline.paths || {})) {
  for (const method of METHODS) {
    const baseOp = pathItem[method];
    if (!baseOp) continue;

    const genOp = generated.paths?.[pathKey]?.[method];
    if (!genOp) continue; // already caught as endpoint-removed

    const baseSchema = getResponseSchema(baseline, baseOp);
    const genSchema = getResponseSchema(generated, genOp);
    if (!baseSchema || !genSchema) continue;

    const baseProps = getSchemaProperties(baseline, baseSchema);
    const genProps = getSchemaProperties(generated, genSchema);
    const baseRequired = new Set(getRequiredFields(baseline, baseSchema));

    for (const field of Object.keys(baseProps)) {
      if (!(field in genProps)) {
        breaking(
          "response-field-removed",
          `${method.toUpperCase()} ${pathKey} — response field "${field}" removed`
        );
      } else {
        // Check type change
        const baseType = baseProps[field].type || baseProps[field].$ref;
        const genType = genProps[field].type || genProps[field].$ref;
        if (baseType && genType && baseType !== genType) {
          breaking(
            "response-field-type-changed",
            `${method.toUpperCase()} ${pathKey} — response field "${field}" type changed: ${baseType} → ${genType}`
          );
        }
      }
    }

    // Check new required fields added to response (non-breaking for responses, skip)
  }
}

// ── 3. New required request fields (breaking for callers) ─

for (const [pathKey, pathItem] of Object.entries(generated.paths || {})) {
  for (const method of METHODS) {
    const genOp = pathItem[method];
    if (!genOp) continue;

    const baseOp = baseline.paths?.[pathKey]?.[method];
    if (!baseOp) continue; // new endpoint — not breaking

    const baseSchema = getRequestSchema(baseline, baseOp);
    const genSchema = getRequestSchema(generated, genOp);
    if (!genSchema) continue;

    const baseRequired = new Set(getRequiredFields(baseline, baseSchema));
    const genRequired = new Set(getRequiredFields(generated, genSchema));

    for (const field of genRequired) {
      if (!baseRequired.has(field)) {
        breaking(
          "request-required-field-added",
          `${method.toUpperCase()} ${pathKey} — new required request field "${field}" (breaks existing callers)`
        );
      }
    }
  }
}

// ── 4. Narrowed enums ─────────────────────────────────────

for (const [name, baseSchema] of Object.entries(baseline.components?.schemas || {})) {
  const genSchema = generated.components?.schemas?.[name];
  if (!genSchema) continue;

  if (Array.isArray(baseSchema.enum) && Array.isArray(genSchema.enum)) {
    const genSet = new Set(genSchema.enum);
    for (const val of baseSchema.enum) {
      if (!genSet.has(val)) {
        breaking(
          "enum-value-removed",
          `Schema "${name}" — enum value "${val}" removed`
        );
      }
    }
  }
}

// ── Report ────────────────────────────────────────────────

if (breaks.length === 0) {
  console.log("✓ No breaking changes detected.\n");
  console.log(`  Baseline:  ${Object.keys(baseline.paths || {}).length} paths`);
  console.log(`  Generated: ${Object.keys(generated.paths || {}).length} paths`);
  process.exit(0);
} else {
  console.log(`✗ ${breaks.length} breaking change(s) detected:\n`);

  const grouped = {};
  for (const b of breaks) {
    (grouped[b.category] = grouped[b.category] || []).push(b);
  }

  for (const [category, items] of Object.entries(grouped)) {
    console.log(`  [${category}] (${items.length})`);
    for (const item of items) {
      console.log(`    ✗ ${item.detail}`);
    }
    console.log();
  }

  process.exit(1);
}
