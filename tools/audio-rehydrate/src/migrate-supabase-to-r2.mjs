#!/usr/bin/env node
// Direct bucket-to-bucket rehydration: copy each missing audio object from the old
// Supabase bucket (cambrian-audio-prod) into R2 at the SAME key the DB already
// references. Because the key is preserved, NO DB write is needed — the track plays
// the moment the object lands and the stream endpoint confirms 200/206.
//
// Source of the work-list is the backend's /health/audio-audit (the 149 missing keys),
// so this needs NO Supabase LIST — only a GET per known key. Supabase egress must be
// un-blocked first (currently HTTP 402 Payment Required at the project level).
//
// Read-only by default. --apply writes to PRODUCTION R2 and requires
// CONFIRM_PRODUCTION_AUDIO_REHYDRATE=I_UNDERSTAND_THIS_WRITES_TO_PRODUCTION_STORAGE.
// Existing R2 objects are never overwritten unless --force.

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import {
  CONFIRM_TOKEN, loadConfig, parseArgs, log, writeCsv,
  backendLogin, audioAudit, storageProbe, streamVerify, r2Put,
} from "./lib.mjs";

const cfg = loadConfig();
const args = parseArgs(process.argv.slice(2));
const OUT = path.resolve(args.output);
fs.mkdirSync(OUT, { recursive: true });

const SB = {
  endpoint: process.env.SUPABASE_S3_ENDPOINT || "https://vjfiakxdnmcqcktyrhik.storage.supabase.co/storage/v1/s3",
  bucket: process.env.SUPABASE_S3_BUCKET || "cambrian-audio-prod",
  key: process.env.SUPABASE_S3_KEY || "",
  secret: process.env.SUPABASE_S3_SECRET || "",
  region: process.env.SUPABASE_S3_REGION || "us-east-1",
};
const MODE = args.apply ? "APPLY" : "DRY-RUN";

function aws(awsArgs) {
  const env = { ...process.env, AWS_ACCESS_KEY_ID: SB.key, AWS_SECRET_ACCESS_KEY: SB.secret, AWS_DEFAULT_REGION: SB.region };
  const r = spawnSync("aws", awsArgs, { encoding: "utf8", env, maxBuffer: 256 * 1024 * 1024, shell: true });
  return { code: r.status, stdout: r.stdout || "", stderr: r.stderr || "" };
}

// HEAD a source object → { exists, length } (dry-run existence/size check).
function supabaseHead(key) {
  const r = aws(["s3api", "head-object", "--bucket", SB.bucket, "--key", `"${key}"`, "--endpoint-url", SB.endpoint, "--output", "json"]);
  if (r.code !== 0) {
    const payment = /402|Payment Required/i.test(r.stderr);
    return { exists: false, length: null, error: payment ? "supabase_402_payment_required" : r.stderr.trim().split("\n").pop() };
  }
  try { return { exists: true, length: JSON.parse(r.stdout).ContentLength ?? null, error: null }; }
  catch { return { exists: true, length: null, error: null }; }
}

function supabaseGet(key, outPath) {
  const r = aws(["s3api", "get-object", "--bucket", SB.bucket, "--key", `"${key}"`, "--endpoint-url", SB.endpoint, `"${outPath}"`]);
  if (r.code !== 0) throw new Error(r.stderr.trim().split("\n").pop() || "supabase get-object failed");
  return fs.existsSync(outPath) ? fs.statSync(outPath).size : 0;
}

async function main() {
  log.step(`Supabase→R2 audio rehydration — mode=${MODE}`);
  log.dim(`  source=${SB.bucket} @ ${SB.endpoint}`);
  log.dim(`  dest=${cfg.bucket} (R2)  backend=${cfg.backend}`);
  if (!SB.key || !SB.secret) { log.err("Set SUPABASE_S3_KEY and SUPABASE_S3_SECRET."); process.exit(2); }

  const token = await backendLogin(cfg);
  const audit = await audioAudit(cfg, token);
  const missing = (audit.missing || []).filter((m) => m.audioUrl && String(m.audioUrl).trim());
  log.ok(`audit: ${audit.missingCount} missing; ${missing.length} have a key to restore`);

  if (args.apply && cfg.confirm !== CONFIRM_TOKEN) {
    log.err(`Refusing to apply: set CONFIRM_PRODUCTION_AUDIO_REHYDRATE=${CONFIRM_TOKEN}`);
    process.exit(3);
  }

  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "cambrian-rehydrate-"));
  const rows = [];
  const auditLog = [];
  let restored = 0, sourceMissing = 0, blocked = 0, skipped = 0, failed = 0;

  const work = args.limit ? missing.slice(0, args.limit) : missing;
  for (const m of work) {
    const key = String(m.audioUrl).trim();
    const rec = { ts: new Date().toISOString(), trackId: m.trackId, title: m.title, key, steps: [] };
    const row = { track_id: m.trackId, title: m.title, key, source_exists: "", source_size: "", action: "", result: "", notes: "" };

    // Source existence/size.
    const head = supabaseHead(key);
    row.source_exists = head.exists; row.source_size = head.length ?? "";
    if (!head.exists) {
      if (head.error === "supabase_402_payment_required") { row.action = "blocked"; row.result = "supabase_402"; row.notes = "Supabase egress blocked (resolve project billing)."; blocked++; }
      else { row.action = "skip"; row.result = "source_missing"; row.notes = head.error || "not in Supabase bucket"; sourceMissing++; }
      rows.push(row); rec.result = row.result; auditLog.push(rec); continue;
    }

    // Already present in R2 (and size matches) → nothing to do.
    const r2Before = await storageProbe(cfg, token, key);
    if (r2Before.exists && (r2Before.length == null || head.length == null || Number(r2Before.length) === Number(head.length)) && !args.force) {
      row.action = "skip"; row.result = "already_in_r2"; skipped++;
      rows.push(row); rec.result = row.result; auditLog.push(rec); continue;
    }

    if (!args.apply) {
      row.action = "would_copy"; row.result = "dry_run";
      rows.push(row); rec.result = row.result; auditLog.push(rec); continue;
    }

    // APPLY: Supabase GET → R2 PUT → verify object → verify stream.
    const tmp = path.join(tmpDir, `${m.trackId}${path.extname(key) || ".bin"}`);
    try {
      const dl = supabaseGet(key, tmp); rec.steps.push(`downloaded ${dl}b`);
      r2Put(cfg.bucket, key, tmp); rec.steps.push("r2_put");
      const after = await storageProbe(cfg, token, key);
      if (!after.exists) { row.action = "copy"; row.result = "failed_upload"; row.notes = "missing after put"; failed++; }
      else if (after.length != null && head.length != null && Number(after.length) !== Number(head.length)) {
        row.action = "copy"; row.result = "failed_upload"; row.notes = `size mismatch r2=${after.length} src=${head.length}`; failed++;
      } else {
        rec.steps.push(`verified_object ${after.length}`);
        const sv = await streamVerify(cfg, m.trackId);
        rec.stream = sv;
        if (sv.ok) { row.action = "copy"; row.result = "playable"; restored++; log.ok(`${m.title} → playable`); }
        else { row.action = "copy"; row.result = "failed_stream_verification"; row.notes = `stream ${sv.status} ${sv.contentType}`; failed++; }
      }
    } catch (e) {
      row.action = "copy"; row.result = "error"; row.notes = String(e.message || e); failed++;
    } finally {
      try { fs.rmSync(tmp, { force: true }); } catch {}
    }
    rec.result = row.result; auditLog.push(rec);
    rows.push(row);
  }
  try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch {}

  writeCsv(path.join(OUT, "supabase-to-r2-report.csv"),
    ["track_id", "title", "key", "source_exists", "source_size", "action", "result", "notes"], rows);
  if (args.apply) fs.writeFileSync(path.join(OUT, "supabase-to-r2-audit-log.jsonl"), auditLog.map((r) => JSON.stringify(r)).join("\n") + "\n", "utf8");

  log.step("Summary");
  console.log(JSON.stringify({ mode: MODE, considered: work.length, restored, skipped_already_in_r2: skipped, source_missing: sourceMissing, supabase_blocked_402: blocked, failed }, null, 2));
  if (blocked > 0) log.err(`Supabase returned 402 for ${blocked} object(s) — resolve project billing, then re-run.`);
  log.ok(`Report: ${path.join(OUT, "supabase-to-r2-report.csv")}`);
}

main().catch((e) => { log.err(e.stack || String(e)); process.exit(1); });
