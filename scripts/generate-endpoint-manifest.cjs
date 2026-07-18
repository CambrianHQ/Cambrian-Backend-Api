#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const openApiPath = path.join(root, "contracts", "openapi.v1.json");
const manifestPath = path.join(root, "contracts", "endpoint-manifest.v1.json");
const controllersDir = path.join(root, "src", "Cambrian.Api", "Controllers");

const spec = JSON.parse(fs.readFileSync(openApiPath, "utf8"));

function findCsFiles(dir) {
  const results = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) results.push(...findCsFiles(full));
    else if (entry.name.endsWith(".cs")) results.push(full);
  }
  return results;
}

function normalizePath(rawPath) {
  if (!rawPath) return "";
  const withSlash = rawPath.startsWith("/") ? rawPath : `/${rawPath.replace(/^\/+/, "")}`;
  const withoutTrailingSlash = withSlash.length > 1 ? withSlash.replace(/\/+$/, "") : withSlash;
  return withoutTrailingSlash.replace(/\{([^}:]+):[^}]+\}/g, "{$1}");
}

function combineRoutes(baseRoute, actionRoute) {
  if (actionRoute.startsWith("/")) return normalizePath(actionRoute);
  return normalizePath([baseRoute, actionRoute].filter(Boolean).join("/"));
}

function parseAttributeArgs(attrText, attrName) {
  const pattern = new RegExp(`\\[${attrName}\\s*(?:\\(([^\\]]*)\\))?\\]`, "gi");
  return [...attrText.matchAll(pattern)].map((match) => match[1] || "");
}

function parseRoles(authorizeArgs) {
  return authorizeArgs
    .flatMap((args) => {
      const match = args.match(/Roles\s*=\s*"([^"]+)"/i);
      return match ? match[1].split(",").map((role) => role.trim()).filter(Boolean) : [];
    })
    .filter(Boolean);
}

function parsePolicies(authorizeArgs) {
  return authorizeArgs
    .flatMap((args) => {
      const explicit = args.match(/Policy\s*=\s*"([^"]+)"/i);
      if (explicit) return [explicit[1].trim()];

      const positional = args.match(/^"([^"]+)"/);
      return positional ? [positional[1].trim()] : [];
    })
    .filter(Boolean);
}

function distinctJoin(values) {
  const distinct = [...new Set(values.filter(Boolean).map((value) => value.trim()).filter(Boolean))];
  distinct.sort((a, b) => a.localeCompare(b));
  return distinct.length > 0 ? distinct.join(",") : undefined;
}

function parseController(filePath) {
  const src = fs.readFileSync(filePath, "utf8");
  const lines = src.split(/\r?\n/);
  const classLine = lines.findIndex((line) => /\bclass\s+\w+/.test(line));
  if (classLine < 0) return [];

  const classPrefix = lines.slice(0, classLine).join("\n");
  const classRoute = (classPrefix.match(/\[Route\s*\(\s*"([^"]*)"\s*\)\]/i) || [])[1] || "";
  const classAuthorizeArgs = parseAttributeArgs(classPrefix, "Authorize");
  const classHasAllowAnonymous = /\[AllowAnonymous\b/i.test(classPrefix);
  const classRequiresCreatorTier = /\[RequireCreatorTier\b/i.test(classPrefix);
  const classIgnored = /\[ApiExplorerSettings\s*\([^)]*IgnoreApi\s*=\s*true/i.test(classPrefix);
  if (classIgnored) return [];

  const actions = [];
  let pendingAttributes = [];

  for (const line of lines.slice(classLine + 1)) {
    const trimmed = line.trim();

    if (trimmed.startsWith("[") && trimmed.endsWith("]")) {
      pendingAttributes.push(trimmed);
      continue;
    }

    if (/^\s*$/.test(trimmed) || trimmed.startsWith("///") || trimmed.startsWith("//")) {
      continue;
    }

    if (!/\bpublic\b/.test(trimmed) || !trimmed.includes("(")) {
      pendingAttributes = [];
      continue;
    }

    const attrText = pendingAttributes.join("\n");
    pendingAttributes = [];

    if (/\[ApiExplorerSettings\s*\([^)]*IgnoreApi\s*=\s*true/i.test(attrText)) {
      continue;
    }

    const httpAttrs = [...attrText.matchAll(/\[Http(Get|Head|Post|Put|Delete|Patch)\s*(?:\(\s*"([^"]*)"\s*\))?\s*\]/gi)];
    if (httpAttrs.length === 0) continue;

    const methodAuthorizeArgs = parseAttributeArgs(attrText, "Authorize");
    const allowAnonymous = classHasAllowAnonymous || /\[AllowAnonymous\b/i.test(attrText);
    const authorizeArgs = [...classAuthorizeArgs, ...methodAuthorizeArgs];
    const requiresAuth = !allowAnonymous && authorizeArgs.length > 0;
    const requiresCreatorTier = classRequiresCreatorTier || /\[RequireCreatorTier\b/i.test(attrText);
    const roles = parseRoles(authorizeArgs);
    const policies = parsePolicies(authorizeArgs);
    const requiresRole = requiresAuth
      ? distinctJoin(roles.length > 0 ? roles : (requiresCreatorTier ? ["Creator"] : []))
      : undefined;
    const requiresPolicy = requiresAuth ? distinctJoin(policies) : undefined;

    for (const httpAttr of httpAttrs) {
      actions.push({
        method: httpAttr[1].toUpperCase(),
        path: combineRoutes(classRoute, httpAttr[2] || ""),
        requiresAuth,
        requiresRole,
        requiresPolicy
      });
    }
  }

  return actions;
}

const controllerSecurity = new Map();
for (const action of findCsFiles(controllersDir).flatMap(parseController)) {
  const key = `${action.method} ${action.path}`;
  const current = controllerSecurity.get(key);
  if (!current) {
    controllerSecurity.set(key, action);
    continue;
  }

  controllerSecurity.set(key, {
    method: action.method,
    path: action.path,
    requiresAuth: current.requiresAuth || action.requiresAuth,
    requiresRole: distinctJoin([current.requiresRole, action.requiresRole].flatMap((value) => value ? value.split(",") : [])),
    requiresPolicy: distinctJoin([current.requiresPolicy, action.requiresPolicy].flatMap((value) => value ? value.split(",") : []))
  });
}

function inferAuth(pathName, method, operation) {
  const key = `${method} ${normalizePath(pathName)}`;
  const actionSecurity = controllerSecurity.get(key);
  if (actionSecurity) {
    return actionSecurity;
  }

  const opSecurity = Array.isArray(operation.security) ? operation.security : null;
  if (opSecurity && opSecurity.length === 0) return { requiresAuth: false };
  if (opSecurity && opSecurity.some((entry) => entry.Bearer)) return { requiresAuth: true };
  if (Array.isArray(spec.security) && spec.security.some((entry) => entry.Bearer)) return { requiresAuth: true };

  return { requiresAuth: false };
}

const endpoints = [];
for (const [pathName, pathItem] of Object.entries(spec.paths || {})) {
  for (const method of ["get", "head", "post", "put", "delete", "patch"]) {
    const operation = pathItem[method];
    if (!operation) continue;

    const upperMethod = method.toUpperCase();
    const auth = inferAuth(pathName, upperMethod, operation);
    const endpoint = {
      method: upperMethod,
      path: pathName,
      requiresAuth: Boolean(auth.requiresAuth),
      tag: Array.isArray(operation.tags) && operation.tags.length > 0 ? operation.tags[0] : "Other"
    };
    if (auth.requiresRole) endpoint.requiresRole = auth.requiresRole;
    if (auth.requiresPolicy) endpoint.requiresPolicy = auth.requiresPolicy;
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
