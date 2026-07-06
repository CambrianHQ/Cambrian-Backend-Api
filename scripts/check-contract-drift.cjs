#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const controllersDir = path.join(root, "src", "Cambrian.Api", "Controllers");
const openApiPath = path.join(root, "contracts", "openapi.v1.json");
const manifestPath = path.join(root, "contracts", "endpoint-manifest.v1.json");

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function findCsFiles(dir) {
  const results = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) results.push(...findCsFiles(full));
    else if (entry.name.endsWith(".cs")) results.push(full);
  }
  return results;
}

function normalizeControllerPath(baseRoute, actionPath) {
  if (actionPath.startsWith("/")) return actionPath.replace(/\{([^}:]+):[^}]+\}/g, "{$1}");
  return ("/" + [baseRoute, actionPath].filter(Boolean).join("/")).replace(/\/+/g, "/").replace(/\/$/, "").replace(/\{([^}:]+):[^}]+\}/g, "{$1}");
}

function parseControllerActions(filePath) {
  const src = fs.readFileSync(filePath, "utf8");
  const routeMatch = src.match(/\[Route\("([^"]*)"\)\]/);
  const baseRoute = routeMatch ? routeMatch[1] : "";
  const lines = src.split(/\r?\n/);
  const actions = [];

  for (let i = 0; i < lines.length; i++) {
    const attrMatch = lines[i].match(/\[Http(Get|Post|Put|Delete|Patch)\s*(?:\("([^"]*)"\))?\s*\]/i);
    if (!attrMatch) continue;

    const httpMethod = attrMatch[1].toUpperCase();
    const actionPath = attrMatch[2] || "";
    let methodLine = i + 1;
    while (methodLine < lines.length && !/public\s+.*\(/.test(lines[methodLine])) methodLine++;

    // Attributes on an action are stacked directly above the method signature, in any
    // order, so scan the whole contiguous attribute block (not just the Http* line) for
    // [ApiExplorerSettings]/[ProducesResponseType] — both can appear before or after Http*.
    let attrBlockStart = i;
    while (attrBlockStart > 0 && /^\s*\[.*\]\s*$/.test(lines[attrBlockStart - 1])) attrBlockStart--;
    const attrBlock = lines.slice(attrBlockStart, methodLine).join("\n");

    // Routes hidden from the public API surface (internal/local-dev proxies, deploy
    // gates) are intentionally absent from openapi.v1.json — mirrors validate-contracts.cjs.
    if (/\[ApiExplorerSettings\s*\(\s*IgnoreApi\s*=\s*true\s*\)\]/i.test(attrBlock)) continue;

    const bodyStart = methodLine;
    let bodyEnd = bodyStart;
    let braceDepth = 0;
    let seenOpen = false;

    for (let j = bodyStart; j < lines.length; j++) {
      const line = lines[j];
      for (const ch of line) {
        if (ch === "{") {
          braceDepth++;
          seenOpen = true;
        }
        if (ch === "}") braceDepth--;
      }
      if (seenOpen && braceDepth === 0) {
        bodyEnd = j;
        break;
      }
    }

    const body = lines.slice(bodyStart, bodyEnd + 1).join("\n");
    let successCode = 200;
    let envelope = "data";
    if (/CreatedResponse\(/.test(body)) successCode = 201;
    else if (/NoContent\(/.test(body)) successCode = 204;
    else if (/StatusCode\(501/.test(body)) successCode = 501;
    else if (/StatusCode\(403/.test(body) && !/OkResponse\(|MessageResponse\(|CreatedResponse\(|NoContent\(/.test(body)) successCode = 403;
    else if (/\bAccepted\(/.test(body) && !/OkResponse\(/.test(body)) successCode = 202;

    // An explicit [ProducesResponseType(..., StatusCodes.StatusXxx)] documenting a 2xx
    // code is the author's stated intent — trust it over the body-shape guess above.
    const declared2xx = [...attrBlock.matchAll(/ProducesResponseType\([^)]*StatusCodes\.Status(\d{3})\w*\)/g)]
      .map((m) => Number(m[1]))
      .find((code) => code >= 200 && code < 300);
    if (declared2xx) successCode = declared2xx;

    if (/MessageResponse\(/.test(body)) envelope = "message";
    else if (/NoContent\(/.test(body)) envelope = "none";
    else if (/OkResponse\(|CreatedResponse\(/.test(body)) envelope = "data";

    actions.push({
      filePath,
      method: httpMethod,
      path: normalizeControllerPath(baseRoute, actionPath),
      successCode,
      envelope
    });
  }

  return actions;
}

const openApi = readJson(openApiPath);
const manifest = readJson(manifestPath);
const controllerActions = findCsFiles(controllersDir).flatMap(parseControllerActions);

let mutableFailures = 0;
function report(message) {
  console.error(`FAIL: ${message}`);
  mutableFailures++;
}

const openApiRouteKeys = new Set();
for (const [pathName, pathItem] of Object.entries(openApi.paths || {})) {
  for (const method of ["get", "post", "put", "delete", "patch"]) {
    if (pathItem[method]) openApiRouteKeys.add(`${method.toUpperCase()} ${pathName}`);
  }
}

const manifestKeys = manifest.endpoints.map((endpoint) => `${endpoint.method.toUpperCase()} ${endpoint.path}`);
const duplicateManifestKeys = manifestKeys.filter((key, index) => manifestKeys.indexOf(key) !== index);
if (duplicateManifestKeys.length > 0) {
  report(`Duplicate manifest entries found: ${[...new Set(duplicateManifestKeys)].join(", ")}`);
}

for (const action of controllerActions) {
  const key = `${action.method} ${action.path}`;
  if (!openApiRouteKeys.has(key)) {
    report(`Method/path parity drift: controller route ${key} is missing from openapi.v1.json`);
    continue;
  }

  const pathItem = openApi.paths[action.path];
  const operation = pathItem?.[action.method.toLowerCase()];
  const openApiSuccess = Object.keys(operation?.responses || {}).find((code) => /^2\d\d$/.test(code));
  if (openApiSuccess && Number(openApiSuccess) !== action.successCode) {
    report(`Status-code parity drift: ${key} controller=${action.successCode} openapi=${openApiSuccess}`);
  }

  // Most actions return IActionResult with no [ProducesResponseType], so Swashbuckle has
  // nothing to introspect and emits a bare "200: OK" with no response body schema at all.
  // That's undocumented, not conflicting — only compare envelopes when openapi.v1.json
  // actually documents a response schema to compare against.
  const schema = operation?.responses?.[openApiSuccess]?.content?.["application/json"]?.schema;
  if (schema && action.successCode !== 204) {
    const ref = schema.$ref || "";
    let openApiEnvelope = "data";
    if (ref.endsWith("ApiResponseOfMessage")) openApiEnvelope = "message";
    else if (schema.properties?.data || ref.includes("ApiResponse")) openApiEnvelope = "data";

    if (openApiEnvelope !== action.envelope) {
      report(`Envelope schema parity drift: ${key} controller=${action.envelope} openapi=${openApiEnvelope}`);
    }
  }
}

for (const key of manifestKeys) {
  if (!openApiRouteKeys.has(key)) {
    report(`Method/path parity drift: manifest route ${key} is missing from openapi.v1.json`);
  }
}

if (mutableFailures > 0) {
  process.exit(1);
}

console.log("Contract drift checks passed.");
