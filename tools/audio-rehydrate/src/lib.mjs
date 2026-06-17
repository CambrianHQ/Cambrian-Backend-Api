// Shared helpers for the Cambrian audio rehydration pipeline.
// No production secrets live here — all credentials come from the environment.

import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";

export const ALLOWED_EXTS = [".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg"];

export const CONTENT_TYPES = {
  ".mp3": "audio/mpeg",
  ".wav": "audio/wav",
  ".flac": "audio/flac",
  ".m4a": "audio/mp4",
  ".aac": "audio/aac",
  ".ogg": "audio/ogg",
};

export const CONFIRM_TOKEN = "I_UNDERSTAND_THIS_WRITES_TO_PRODUCTION_STORAGE";

// ── config from env ──────────────────────────────────────────────────────────
export function loadConfig() {
  return {
    backend: (process.env.REHYDRATE_BACKEND || "https://cambrian-backend-api.onrender.com").replace(/\/+$/, ""),
    bucket: process.env.REHYDRATE_R2_BUCKET || "cambrainaudio",
    adminEmail: process.env.REHYDRATE_ADMIN_EMAIL || "",
    adminPassword: process.env.REHYDRATE_ADMIN_PASSWORD || "",
    databaseUrl: process.env.DATABASE_URL || "",
    confirm: process.env.CONFIRM_PRODUCTION_AUDIO_REHYDRATE || "",
  };
}

// ── logging ──────────────────────────────────────────────────────────────────
const C = { reset: "\x1b[0m", dim: "\x1b[2m", red: "\x1b[31m", green: "\x1b[32m", yellow: "\x1b[33m", cyan: "\x1b[36m" };
export const log = {
  info: (m) => console.log(m),
  step: (m) => console.log(`${C.cyan}▸ ${m}${C.reset}`),
  ok: (m) => console.log(`${C.green}✓ ${m}${C.reset}`),
  warn: (m) => console.log(`${C.yellow}! ${m}${C.reset}`),
  err: (m) => console.log(`${C.red}✗ ${m}${C.reset}`),
  dim: (m) => console.log(`${C.dim}${m}${C.reset}`),
};

// ── CLI args ─────────────────────────────────────────────────────────────────
export function parseArgs(argv) {
  const args = { localRoots: [], backupRoots: [], output: "reports/audio", dryRun: false, apply: false, force: false, scanOnly: false, limit: 0 };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--local-root") args.localRoots.push(argv[++i]);
    else if (a === "--output") args.output = argv[++i];
    else if (a === "--dry-run") args.dryRun = true;
    else if (a === "--apply") args.apply = true;
    else if (a === "--force") args.force = true;
    else if (a === "--scan-only") args.scanOnly = true;
    else if (a === "--backup-root") args.backupRoots.push(argv[++i]);
    else if (a === "--limit") args.limit = parseInt(argv[++i], 10) || 0;
  }
  return args;
}

// ── CSV ──────────────────────────────────────────────────────────────────────
function csvCell(v) {
  if (v === null || v === undefined) return "";
  const s = String(v);
  return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}
export function writeCsv(filePath, columns, rows) {
  const lines = [columns.join(",")];
  for (const r of rows) lines.push(columns.map((c) => csvCell(r[c])).join(","));
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, lines.join("\n") + "\n", "utf8");
}

// ── hashing / normalization ──────────────────────────────────────────────────
export function sha256File(filePath) {
  const h = crypto.createHash("sha256");
  h.update(fs.readFileSync(filePath));
  return h.digest("hex");
}

// Normalize a string for fuzzy comparison: lowercase, drop extension-ish/parenthetical
// suffixes, collapse non-alphanumerics to single spaces.
export function normalize(s) {
  if (!s) return "";
  return String(s)
    .toLowerCase()
    .replace(/\.(mp3|wav|flac|m4a|aac|ogg)$/i, "")
    .replace(/\s*\(\d+\)\s*$/, "") // trailing " (1)"
    .replace(/^track\d+[-_\s]+/i, "") // leading "Track3-"
    .replace(/[_\-]+/g, " ")
    .replace(/[^a-z0-9]+/g, " ")
    .trim()
    .replace(/\s+/g, " ");
}

export function titleGuessFromFilename(filename) {
  const base = filename.replace(/\.(mp3|wav|flac|m4a|aac|ogg)$/i, "");
  return base.replace(/\s*\(\d+\)\s*$/, "").replace(/^track\d+[-_\s]+/i, "").replace(/[_]+/g, " ").trim();
}

export function creatorGuessFromPath(filePath) {
  // Parent directory name is a weak creator hint (e.g. tracks/<creator>/file.wav).
  const parent = path.basename(path.dirname(filePath));
  return /^(downloads|desktop|documents|audio|tracks|uploads|test-tracks|cambrian-test-songs)$/i.test(parent) ? "" : parent;
}

// Minimal RIFF/WAVE duration probe (PCM). Returns seconds or null. No external deps.
export function wavDurationSeconds(filePath) {
  try {
    const fd = fs.openSync(filePath, "r");
    const buf = Buffer.alloc(4096);
    const read = fs.readSync(fd, buf, 0, 4096, 0);
    fs.closeSync(fd);
    if (read < 12 || buf.toString("ascii", 0, 4) !== "RIFF" || buf.toString("ascii", 8, 12) !== "WAVE") return null;
    let off = 12, byteRate = 0;
    while (off + 8 <= read) {
      const id = buf.toString("ascii", off, off + 4);
      const size = buf.readUInt32LE(off + 4);
      if (id === "fmt ") byteRate = buf.readUInt32LE(off + 16);
      if (id === "data" && byteRate > 0) return +(size / byteRate).toFixed(2);
      off += 8 + size + (size % 2);
    }
    return null;
  } catch {
    return null;
  }
}

export function durationSeconds(filePath, ext) {
  if (ext === ".wav") return wavDurationSeconds(filePath);
  return null; // compressed formats need ffprobe (absent); duration is the weakest match tier
}

// ── backend (admin-authenticated diagnostics + anonymous stream verify) ───────
export async function backendLogin(cfg) {
  if (!cfg.adminEmail || !cfg.adminPassword) throw new Error("REHYDRATE_ADMIN_EMAIL / REHYDRATE_ADMIN_PASSWORD not set");
  const res = await fetch(`${cfg.backend}/auth/login`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: cfg.adminEmail, password: cfg.adminPassword }),
  });
  if (!res.ok) throw new Error(`admin login failed: HTTP ${res.status}`);
  const j = await res.json();
  const token = j?.data?.token ?? j?.token ?? j?.data?.accessToken;
  if (!token) throw new Error("admin login: no token in response");
  return token;
}

export async function audioAudit(cfg, token) {
  const res = await fetch(`${cfg.backend}/health/audio-audit`, { headers: { authorization: `Bearer ${token}` } });
  if (!res.ok) throw new Error(`audio-audit failed: HTTP ${res.status}`);
  return res.json();
}

// Authoritative existence oracle: HEAD the key through the backend's own R2 connection.
export async function storageProbe(cfg, token, key) {
  const url = `${cfg.backend}/health/storage-probe?key=${encodeURIComponent(key)}`;
  const res = await fetch(url, { headers: { authorization: `Bearer ${token}` } });
  if (!res.ok) throw new Error(`storage-probe failed: HTTP ${res.status}`);
  const j = await res.json();
  return { exists: !!j.headObjectOk, length: j.sampleLength ?? null, contentType: j.sampleContentType ?? null, error: j.headObjectError ?? null };
}

// Verify playback the way a browser would: a Range request to the stream endpoint.
export async function streamVerify(cfg, trackId) {
  const url = `${cfg.backend}/stream/${trackId}/audio`;
  const res = await fetch(url, { headers: { Range: "bytes=0-1023" } });
  const ct = res.headers.get("content-type") || "";
  const ok = (res.status === 200 || res.status === 206) && /^audio\//i.test(ct);
  return { ok, status: res.status, contentType: ct };
}

// ── R2 via wrangler (OAuth) — no separate S3 credentials required ─────────────
function wrangler(argString) {
  const r = spawnSync(`wrangler ${argString}`, { shell: true, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  return { code: r.status, stdout: r.stdout || "", stderr: r.stderr || "" };
}

export function r2Put(bucket, key, filePath) {
  const r = wrangler(`r2 object put "${bucket}/${key}" --file "${filePath}" --remote`);
  if (r.code !== 0) throw new Error(`wrangler put failed (${r.code}): ${r.stderr.trim() || r.stdout.trim()}`);
  return true;
}

// ── Neon DB (pg) ─────────────────────────────────────────────────────────────
// Direct DB access is OPTIONAL. It is only reachable where outbound :5432 is open
// (often blocked on dev networks). Fail fast so callers can fall back to the
// backend API (which proxies the DB). The dominant rehydration path uploads to the
// key the row ALREADY references, so no DB write is needed at all.
export async function withDb(cfg, fn) {
  const { default: pg } = await import("pg");
  const client = new pg.Client({
    connectionString: cfg.databaseUrl,
    ssl: { rejectUnauthorized: false },
    connectionTimeoutMillis: 8000,
    query_timeout: 30000,
  });
  await client.connect();
  try {
    return await fn(client);
  } finally {
    await client.end();
  }
}

export async function dbReachable(cfg) {
  if (!cfg.databaseUrl) return false;
  try {
    return await withDb(cfg, async (db) => { await db.query("SELECT 1"); return true; });
  } catch {
    return false;
  }
}

// Backend-proxied track metadata (works where :5432 is blocked). Pages the public
// catalog. Returns a Map keyed by lowercased trackId.
export async function catalogAll(cfg) {
  const byId = new Map();
  const pageSize = 60;
  for (let page = 1; page <= 50; page++) {
    const res = await fetch(`${cfg.backend}/catalog?page=${page}&pageSize=${pageSize}&sort=newest`);
    if (!res.ok) break;
    const j = await res.json();
    const items = Array.isArray(j?.data) ? j.data : (j?.data?.items ?? []);
    if (!items.length) break;
    for (const t of items) {
      byId.set(String(t.id).toLowerCase(), {
        title: t.title ?? t.name ?? "",
        creator_username: t.creatorSlug ?? "",
        creator_id: t.creatorId ?? "",
        creator_display: t.artist ?? "",
        visibility: t.visibility ?? "",
        status: t.status ?? "",
        created_at: t.createdAt ?? "",
      });
    }
    if (items.length < pageSize) break;
  }
  return byId;
}

export async function fetchTracks(cfg) {
  return withDb(cfg, async (db) => {
    const { rows } = await db.query(`
      SELECT t."Id"              AS track_id,
             t."CambrianTrackId" AS cambrian_track_id,
             t."Title"           AS title,
             t."AudioUrl"        AS current_storage_key,
             t."CoverArtUrl"     AS cover_art_url,
             t."Status"          AS status,
             t."Visibility"      AS visibility,
             t."CreatorId"       AS creator_id,
             t."CreatedAt"       AS created_at,
             c."Username"        AS creator_username,
             c."DisplayName"     AS creator_display
      FROM "Tracks" t
      LEFT JOIN "Creators" c ON c."Id" = t."CreatorUuid"
      ORDER BY t."CreatedAt"`);
    return rows;
  });
}

// Apply-only: point a track row at a (verified) R2 object key.
export async function updateAudioKey(cfg, trackId, key) {
  return withDb(cfg, async (db) => {
    const r = await db.query(`UPDATE "Tracks" SET "AudioUrl" = $1 WHERE "Id" = $2`, [key, trackId]);
    return r.rowCount;
  });
}
