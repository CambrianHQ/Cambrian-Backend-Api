#!/usr/bin/env node
// ──────────────────────────────────────────────────────────
// Cambrian Backend — Architecture Compliance Validator
// Enforces governance/backend-policy.v1.json rules at CI time
// ──────────────────────────────────────────────────────────

"use strict";

const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const CONTRACT = path.join(ROOT, "contracts", "openapi.v1.json");
const POLICY = path.join(ROOT, "governance", "backend-policy.v1.json");
const CONTROLLERS_DIR = path.join(ROOT, "src", "Cambrian.Api", "Controllers");
const SERVICES_DIR = path.join(ROOT, "src", "Cambrian.Application");

// ── Helpers ───────────────────────────────────────────────

function readText(filePath) {
  return fs.readFileSync(filePath, "utf-8");
}

function findCsFiles(dir) {
  if (!fs.existsSync(dir)) return [];
  const results = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) results.push(...findCsFiles(full));
    else if (entry.name.endsWith(".cs")) results.push(full);
  }
  return results;
}

function rel(filePath) {
  return path.relative(ROOT, filePath).replace(/\\/g, "/");
}

// Strip // line comments so mentions in prose (e.g. "one scoped DbContext, no
// concurrent queries") don't get misread as code referencing the type.
function stripLineComments(src) {
  return src.replace(/\/\/[^\n]*/g, "");
}

// ── Checks ────────────────────────────────────────────────

const violations = [];

function fail(rule, file, message) {
  violations.push({ rule, file: file ? rel(file) : null, message });
}

// 1. Contract file must exist
function checkContractExists() {
  if (!fs.existsSync(CONTRACT)) {
    fail("openapi-contract-required", null, "contracts/openapi.v1.json is missing");
    return null;
  }
  try {
    return JSON.parse(readText(CONTRACT));
  } catch {
    fail("openapi-contract-required", null, "contracts/openapi.v1.json is not valid JSON");
    return null;
  }
}

// 2. Controllers must not use DbContext directly
function checkNoDbContextInControllers(controllerFiles) {
  for (const f of controllerFiles) {
    const src = stripLineComments(readText(f));
    if (/CambrianDbContext|DbContext/i.test(src)) {
      fail("no-direct-db-access-from-controllers", f, "Controller references DbContext directly");
    }
  }
}

// 3. Controllers must not expose domain entities (must use DTOs)
function checkNoDomainEntitiesInControllers(controllerFiles) {
  for (const f of controllerFiles) {
    const src = readText(f);
    if (/using\s+Cambrian\.Domain\.Entities/.test(src)) {
      fail("dto-required", f, "Controller imports domain entities — use DTOs instead");
    }
  }
}

// 4. Admin routes must require [Authorize(Roles = "Admin")]
function checkAdminAuth(controllerFiles) {
  for (const f of controllerFiles) {
    const name = path.basename(f);
    if (!/admin/i.test(name)) continue;

    const src = readText(f);
    // Accept class-level or method-level Authorize with Admin role
    const hasAdminAuth =
      /\[Authorize\s*\(\s*Roles\s*=\s*"Admin"\s*\)\s*\]/i.test(src);
    if (!hasAdminAuth) {
      fail(
        "admin-endpoints-require-role",
        f,
        'Admin controller is missing [Authorize(Roles = "Admin")]'
      );
    }
  }
}

// 5. Protected controllers must have [Authorize]
function checkProtectedAuth(controllerFiles) {
  // Controllers that should require authentication
  const protectedPatterns = [
    /library/i,
    /payout/i,
    /upload/i,
    /checkout/i,
    /settings/i,
    /wallet/i,
    /subscription/i,
  ];

  for (const f of controllerFiles) {
    const name = path.basename(f);
    const isProtected = protectedPatterns.some((p) => p.test(name));
    if (!isProtected) continue;

    const src = readText(f);
    const hasAuth = /\[Authorize/.test(src);
    if (!hasAuth) {
      fail(
        "jwt-required-for-protected-routes",
        f,
        "Protected controller is missing [Authorize] attribute"
      );
    }
  }
}

// 6. Controllers must delegate to services (no inline business logic)
function checkControllersDelegateToServices(controllerFiles) {
  for (const f of controllerFiles) {
    const src = readText(f);

    // Flag if controller has LINQ (Where/Select/OrderBy) or raw SQL
    const hasBusinessLogic =
      /\.(Where|Select|OrderBy|GroupBy|FirstOrDefault|SaveChanges|ExecuteSqlRaw)\s*\(/m.test(
        src
      );
    if (hasBusinessLogic) {
      fail(
        "controller-layer-only-http",
        f,
        "Controller contains business logic — delegate to a service"
      );
    }
  }
}

// 7. Stripe webhook must have idempotency checks
function checkWebhookIdempotency(controllerFiles) {
  for (const f of controllerFiles) {
    const name = path.basename(f);
    if (!/webhook/i.test(name)) continue;

    const src = readText(f);

    // Check for signature verification
    const hasSignatureCheck =
      /Stripe-Signature|ConstructEvent|stripe\.webhooks/i.test(src);

    // Also accept if the webhook service handles it — look for service delegation
    const delegatesToService =
      /HandleStripe|ProcessWebhook|HandleEvent/i.test(src);

    if (!hasSignatureCheck && !delegatesToService) {
      fail(
        "stripe-events-idempotent",
        f,
        "Webhook controller has no Stripe signature verification or service delegation"
      );
    }

    // Check the webhook service itself for idempotency
    const serviceFiles = findCsFiles(SERVICES_DIR);
    for (const sf of serviceFiles) {
      const sname = path.basename(sf);
      if (!/webhook/i.test(sname)) continue;

      const ssrc = readText(sf);

      // Skip interfaces — they are not implementations
      if (/^\s*public\s+interface\s+/m.test(ssrc)) continue;

      // Look for patterns indicating idempotency: event ID tracking, duplicate check
      const hasIdempotency =
        /[Ee]vent[Ii]d|idempoten|[Dd]uplicate|[Pp]rocessed|[Aa]lready/i.test(
          ssrc
        );

      // Check if it's just a stub
      const isStub =
        /return\s+Task\.CompletedTask\s*;/.test(ssrc) &&
        ssrc.split("\n").length < 20;

      if (isStub) {
        fail(
          "stripe-events-idempotent",
          sf,
          "Webhook service is a stub — implement event processing with idempotency"
        );
      } else if (!hasIdempotency) {
        fail(
          "stripe-events-idempotent",
          sf,
          "Webhook service has no idempotency check (no event ID deduplication)"
        );
      }
    }
  }
}

// 8. All controller routes must exist in OpenAPI contract
function checkRoutesInContract(controllerFiles, openApi) {
  if (!openApi) return;

  const contractPaths = Object.keys(openApi.paths || {}).map((p) =>
    p.toLowerCase()
  );

  for (const f of controllerFiles) {
    const src = readText(f);

    // Extract class-level [Route("...")] 
    const routeMatch = src.match(/\[Route\("([^"]*)"\)\]/);
    const baseRoute = routeMatch ? routeMatch[1] : "";

    // Extract all action-level [HttpGet("...")] / [HttpPost("...")] etc.
    const actionRoutes = [
      ...src.matchAll(
        /\[Http(Get|Head|Post|Put|Delete|Patch)\s*(?:\("([^"]*)"\))?\s*\]/gi
      ),
    ];

    for (const match of actionRoutes) {
      const actionPath = match[2] || "";

      // Skip routes hidden from API explorer (e.g. internal/local-dev proxy endpoints)
      const precedingContext = src.substring(Math.max(0, match.index - 200), match.index);
      if (/\[ApiExplorerSettings\s*\(\s*IgnoreApi\s*=\s*true\s*\)\]/i.test(precedingContext)) {
        continue;
      }

      // In ASP.NET Core, a template starting with "/" is absolute
      // and overrides the controller-level [Route] prefix.
      let fullPath;
      if (actionPath.startsWith("/")) {
        fullPath = actionPath;
      } else {
        fullPath = "/" + [baseRoute, actionPath]
          .filter(Boolean)
          .join("/")
          .replace(/\/+/g, "/")
          .replace(/\/$/, "");
      }

      // Normalize path parameters: {id:guid} → {id}, {trackId:int} → {trackId}
      fullPath = fullPath.replace(/\{([^}:]+):[^}]+\}/g, "{$1}");

      const normalized = fullPath.toLowerCase();

      if (!contractPaths.includes(normalized)) {
        fail(
          "openapi-contract-required",
          f,
          `Route ${fullPath} is not defined in openapi.v1.json`
        );
      }
    }
  }
}

// ── Run ───────────────────────────────────────────────────

console.log("╔══════════════════════════════════════════════╗");
console.log("║  Cambrian Backend Architecture Compliance    ║");
console.log("╚══════════════════════════════════════════════╝\n");

// Load policy
if (!fs.existsSync(POLICY)) {
  console.error("✗ governance/backend-policy.v1.json not found");
  process.exit(1);
}
const policy = JSON.parse(readText(POLICY));
console.log(`Policy: ${policy.project} (${policy.version})`);
console.log(`Rules:  ${policy.rules.length}\n`);

// Load contract
const openApi = checkContractExists();
if (openApi) {
  const routeCount = Object.keys(openApi.paths || {}).length;
  console.log(`Contract: ${routeCount} routes in openapi.v1.json`);
}

// Discover controllers
const controllerFiles = findCsFiles(CONTROLLERS_DIR);
console.log(`Controllers: ${controllerFiles.length} files found\n`);

// Run all checks
checkNoDbContextInControllers(controllerFiles);
checkNoDomainEntitiesInControllers(controllerFiles);
checkAdminAuth(controllerFiles);
checkProtectedAuth(controllerFiles);
checkControllersDelegateToServices(controllerFiles);
checkWebhookIdempotency(controllerFiles);
checkRoutesInContract(controllerFiles, openApi);

// ── Report ────────────────────────────────────────────────

if (violations.length === 0) {
  console.log("✓ All architecture compliance checks passed.\n");
  process.exit(0);
} else {
  console.log(`✗ ${violations.length} violation(s) found:\n`);

  const grouped = {};
  for (const v of violations) {
    (grouped[v.rule] = grouped[v.rule] || []).push(v);
  }

  for (const [rule, items] of Object.entries(grouped)) {
    console.log(`  [${rule}]`);
    for (const item of items) {
      const loc = item.file ? `  ${item.file}` : "";
      console.log(`    ✗ ${item.message}${loc}`);
    }
    console.log();
  }

  // Determine if any violations have enforcement: fail-build in policy
  const failBuildRules = new Set(
    policy.rules
      .filter((r) => r.enforcement === "fail-build")
      .map((r) => r.name)
  );

  const blocking = violations.filter((v) => failBuildRules.has(v.rule));

  if (blocking.length > 0) {
    console.log(
      `✗ ${blocking.length} blocking violation(s) (enforcement: fail-build)`
    );
    process.exit(1);
  } else {
    console.log(
      "⚠ Violations found but none are marked fail-build — passing with warnings."
    );
    process.exit(0);
  }
}
