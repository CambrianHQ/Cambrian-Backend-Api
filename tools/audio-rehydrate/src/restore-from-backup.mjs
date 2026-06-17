#!/usr/bin/env node
// Key-preserving restore from a local bucket backup (C:\Users\logan\r2-backup) into R2.
//
// The backup mirrors the bucket: a file at <root>/tracks/<c>/<f>.wav maps to the R2 key
// tracks/<c>/<f>.wav — the EXACT key the DB already references. So restoring = upload to
// the same key. No Supabase (bypasses the 402), no DB writes, no matching ambiguity.
//
// Work-list = the 149 missing audio keys from /health/audio-audit. Read-only by default.
// --apply writes to PRODUCTION R2 and requires
//   CONFIRM_PRODUCTION_AUDIO_REHYDRATE=I_UNDERSTAND_THIS_WRITES_TO_PRODUCTION_STORAGE
// Already-present R2 objects are skipped (size match) unless --force.

import fs from "node:fs";
import path from "node:path";
import {
  CONFIRM_TOKEN, loadConfig, parseArgs, log, writeCsv,
  backendLogin, audioAudit, storageProbe, streamVerify, r2Put,
} from "./lib.mjs";

const cfg = loadConfig();
const args = parseArgs(process.argv.slice(2));
const OUT = path.resolve(args.output);
fs.mkdirSync(OUT, { recursive: true });

// Backup roots tried in order; first one containing the exact key wins. The Supabase
// export is the most complete (147/149); r2-backup is a partial earlier copy.
const BACKUP_ROOTS = args.backupRoots?.length
  ? args.backupRoots
  : (process.env.BACKUP_ROOTS ? process.env.BACKUP_ROOTS.split(";") : [
      "C:\\Users\\logan\\supabase-audio-download",
      "C:\\Users\\logan\\r2-backup",
    ]);
const MODE = args.apply ? "APPLY" : "DRY-RUN";

function localPathForKey(key) {
  for (const root of BACKUP_ROOTS) {
    const p = path.join(root, ...key.split("/")); // key uses '/'; join handles Windows sep
    if (fs.existsSync(p)) return p;
  }
  return null;
}

async function main() {
  log.step(`Restore from local backup → R2 — mode=${MODE}`);
  log.dim(`  backups=${BACKUP_ROOTS.join(" , ")}  dest=${cfg.bucket} (R2)  backend=${cfg.backend}`);
  if (!BACKUP_ROOTS.some((r) => fs.existsSync(r))) { log.err(`No backup root exists: ${BACKUP_ROOTS.join(", ")}`); process.exit(2); }

  const token = await backendLogin(cfg);
  const audit = await audioAudit(cfg, token);
  const missing = (audit.missing || []).filter((m) => m.audioUrl && String(m.audioUrl).trim());
  log.ok(`audit: ${audit.missingCount} missing; ${missing.length} have a key to restore`);

  if (args.apply && cfg.confirm !== CONFIRM_TOKEN) {
    log.err(`Refusing to apply: set CONFIRM_PRODUCTION_AUDIO_REHYDRATE=${CONFIRM_TOKEN}`);
    process.exit(3);
  }

  const rows = [];
  const auditLog = [];
  let restored = 0, skipped = 0, sourceMissing = 0, failed = 0, wouldRestore = 0;

  const work = args.limit ? missing.slice(0, args.limit) : missing;
  for (const m of work) {
    const key = String(m.audioUrl).trim();
    const local = localPathForKey(key);
    const row = { track_id: m.trackId, title: m.title, key, local_path: local || "", local_size: "", action: "", result: "", notes: "" };
    const rec = { ts: new Date().toISOString(), trackId: m.trackId, title: m.title, key, local, steps: [] };

    if (!local) {
      row.action = "skip"; row.result = "source_missing"; row.notes = "not in any local backup"; sourceMissing++;
      rows.push(row); rec.result = row.result; auditLog.push(rec); continue;
    }
    const localSize = fs.statSync(local).size;
    row.local_size = localSize;
    if (localSize === 0) {
      row.action = "skip"; row.result = "source_zero_bytes"; sourceMissing++;
      rows.push(row); rec.result = row.result; auditLog.push(rec); continue;
    }

    // Already in R2 with matching size?
    const before = await storageProbe(cfg, token, key);
    if (before.exists && (before.length == null || Number(before.length) === localSize) && !args.force) {
      row.action = "skip"; row.result = "already_in_r2"; skipped++;
      rows.push(row); rec.result = row.result; auditLog.push(rec); continue;
    }

    if (!args.apply) {
      row.action = "would_restore"; row.result = "dry_run"; wouldRestore++;
      rows.push(row); rec.result = row.result; auditLog.push(rec); continue;
    }

    // APPLY: upload to the existing key → verify object → verify stream.
    try {
      r2Put(cfg.bucket, key, local); rec.steps.push("r2_put");
      const after = await storageProbe(cfg, token, key);
      if (!after.exists) { row.action = "upload"; row.result = "failed_upload"; row.notes = "missing after put"; failed++; }
      else if (after.length != null && Number(after.length) !== localSize) {
        row.action = "upload"; row.result = "failed_upload"; row.notes = `size mismatch r2=${after.length} local=${localSize}`; failed++;
      } else {
        rec.steps.push(`verified_object ${after.length}`);
        const sv = await streamVerify(cfg, m.trackId);
        rec.stream = sv;
        if (sv.ok) { row.action = "upload"; row.result = "playable"; restored++; log.ok(`${m.title} → playable (${(localSize / 1e6).toFixed(1)}MB)`); }
        else { row.action = "upload"; row.result = "failed_stream_verification"; row.notes = `stream ${sv.status} ${sv.contentType}`; failed++; log.warn(`${m.title} → stream ${sv.status}`); }
      }
    } catch (e) {
      row.action = "upload"; row.result = "error"; row.notes = String(e.message || e); failed++;
      log.err(`${m.title} → ${e.message || e}`);
    }
    rec.result = row.result; auditLog.push(rec);
    rows.push(row);
  }

  writeCsv(path.join(OUT, "restore-from-backup-report.csv"),
    ["track_id", "title", "key", "local_path", "local_size", "action", "result", "notes"], rows);
  if (args.apply) fs.writeFileSync(path.join(OUT, "restore-from-backup-audit-log.jsonl"), auditLog.map((r) => JSON.stringify(r)).join("\n") + "\n", "utf8");

  log.step("Summary");
  console.log(JSON.stringify({
    mode: MODE, considered: work.length,
    restored, would_restore: wouldRestore, skipped_already_in_r2: skipped, source_missing: sourceMissing, failed,
  }, null, 2));
  log.ok(`Report: ${path.join(OUT, "restore-from-backup-report.csv")}`);
}

main().catch((e) => { log.err(e.stack || String(e)); process.exit(1); });
