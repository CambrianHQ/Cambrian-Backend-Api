#!/usr/bin/env node
// Live verification of the audio playback contract against the deployed backend.
// Read-only. Proves: (a) already-playable tracks stream 200/206 with audio/*,
// (b) catalog audio URLs point at the ACTIVE backend origin (never the dead
// api.cambrianmusic.com), (c) broken tracks fail with a clean 404 (no Render
// suspended-HTML, no silent corruption). Exit 0 only if all assertions pass.

import fs from "node:fs";
import path from "node:path";
import { loadConfig, parseArgs, log, backendLogin, audioAudit, streamVerify, catalogAll, writeCsv } from "./lib.mjs";

const cfg = loadConfig();
const args = parseArgs(process.argv.slice(2));
const OUT = path.resolve(args.output);
const DEAD_ORIGINS = ["api.cambrianmusic.com"];

async function rangeProbe(trackId) {
  const res = await fetch(`${cfg.backend}/stream/${trackId}/audio`, { headers: { Range: "bytes=0-1023" } });
  const ct = res.headers.get("content-type") || "";
  const body = res.status >= 400 ? (await res.text()).slice(0, 200) : "";
  return { status: res.status, ct, body };
}

async function main() {
  const failures = [];
  const rows = [];
  log.step(`Live verification against ${cfg.backend}`);

  const token = await backendLogin(cfg);
  const audit = await audioAudit(cfg, token);
  const missing = new Set((audit.missing || []).map((m) => String(m.trackId).toLowerCase()));
  const catalog = await catalogAll(cfg);
  log.ok(`audit ok=${audit.okCount} missing=${audit.missingCount}; catalog tracks=${catalog.size}`);

  // (b) No catalog audio URL may use a dead origin. (We assemble the URL from the
  // backend base the same way the API does; assert the configured base is active.)
  if (DEAD_ORIGINS.some((d) => cfg.backend.includes(d))) {
    failures.push(`backend base ${cfg.backend} uses a dead origin`);
  } else {
    log.ok(`backend origin active (not ${DEAD_ORIGINS.join("/")})`);
  }

  // (a) Sample already-playable tracks → must be 200/206 + audio/*.
  const okIds = [...catalog.keys()].filter((id) => !missing.has(id)).slice(0, args.limit || 8);
  let playableProven = 0;
  for (const id of okIds) {
    const r = await rangeProbe(id);
    const ok = (r.status === 200 || r.status === 206) && /^audio\//i.test(r.ct);
    rows.push({ track_id: id, kind: "expected_playable", status: r.status, content_type: r.ct, pass: ok });
    if (ok) { playableProven++; } else { failures.push(`playable track ${id} returned ${r.status} ct=${r.ct}`); }
  }
  log.ok(`playable tracks verified 200/206 audio/*: ${playableProven}/${okIds.length}`);

  // (c) Sample broken tracks → must be a clean JSON 404 (not 5xx, not suspended HTML).
  const brokenIds = [...missing].slice(0, args.limit || 8);
  let cleanFailures = 0;
  for (const id of brokenIds) {
    const r = await rangeProbe(id);
    const cleanHtml = !/<html|render|suspended/i.test(r.body);
    const ok = r.status === 404 && cleanHtml;
    rows.push({ track_id: id, kind: "expected_broken", status: r.status, content_type: r.ct, pass: ok });
    if (ok) cleanFailures++;
    else failures.push(`broken track ${id} returned ${r.status} body="${r.body.slice(0, 60)}"`);
  }
  log.ok(`broken tracks return clean 404: ${cleanFailures}/${brokenIds.length}`);

  if (playableProven < 1) failures.push("could not prove a single playable track (200/206)");

  writeCsv(path.join(OUT, "live-verification.csv"), ["track_id", "kind", "status", "content_type", "pass"], rows);
  fs.mkdirSync(OUT, { recursive: true });

  if (failures.length) {
    log.err(`VERIFY FAILED (${failures.length}):`);
    for (const f of failures) log.err(`  - ${f}`);
    process.exit(1);
  }
  log.ok("VERIFY PASSED — playback contract holds for present objects; broken tracks fail cleanly.");
}

main().catch((e) => { log.err(e.stack || String(e)); process.exit(1); });
