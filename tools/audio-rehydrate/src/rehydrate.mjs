#!/usr/bin/env node
// Cambrian audio rehydration / import pipeline.
//
// Core principle — a track is playable only when ALL of:
//   1. the DB track row has the correct storage key (Track.AudioUrl)
//   2. the R2 object exists at that key
//   3. GET /stream/{trackId}/audio returns 200/206 with an audio/* content-type
//
// Safety: read-only by default. --apply writes to PRODUCTION and additionally
// requires CONFIRM_PRODUCTION_AUDIO_REHYDRATE=I_UNDERSTAND_THIS_WRITES_TO_PRODUCTION_STORAGE.
// Only exact/high-confidence matches are ever uploaded; existing playable tracks
// are skipped unless --force; R2 objects are never overwritten without --force.

import fs from "node:fs";
import path from "node:path";
import {
  ALLOWED_EXTS, CONTENT_TYPES, CONFIRM_TOKEN, loadConfig, parseArgs, log, writeCsv,
  sha256File, normalize, titleGuessFromFilename, creatorGuessFromPath, durationSeconds,
  backendLogin, audioAudit, storageProbe, streamVerify, r2Put, fetchTracks, updateAudioKey,
  dbReachable, catalogAll,
} from "./lib.mjs";

const cfg = loadConfig();
const args = parseArgs(process.argv.slice(2));
const OUT = path.resolve(args.output);
fs.mkdirSync(OUT, { recursive: true });

const MODE = args.apply ? "APPLY" : args.scanOnly ? "SCAN-ONLY" : "DRY-RUN";

function defaultLocalRoots() {
  // Fall back to ./local-tracks if it exists; otherwise the operator must pass --local-root.
  const lt = path.resolve("local-tracks");
  return fs.existsSync(lt) ? [lt] : [];
}

// ── 1. local audio manifest ───────────────────────────────────────────────────
function scanLocalAudio(roots) {
  const files = [];
  const seen = new Set();
  const walk = (dir) => {
    let entries;
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const e of entries) {
      const full = path.join(dir, e.name);
      if (e.isDirectory()) {
        if (/node_modules|\.git|bin|obj/i.test(e.name)) continue;
        walk(full);
      } else {
        const ext = path.extname(e.name).toLowerCase();
        if (!ALLOWED_EXTS.includes(ext)) continue;
        const real = fs.realpathSync(full);
        if (seen.has(real)) continue;
        seen.add(real);
        const stat = fs.statSync(full);
        files.push({
          local_path: full,
          filename: e.name,
          normalized_filename: normalize(e.name),
          extension: ext,
          file_size: stat.size,
          sha256: stat.size > 0 ? sha256File(full) : "",
          duration_seconds: durationSeconds(full, ext) ?? "",
          title_guess: titleGuessFromFilename(e.name),
          creator_guess: creatorGuessFromPath(full),
        });
      }
    }
  };
  for (const r of roots) walk(path.resolve(r));
  files.sort((a, b) => a.local_path.localeCompare(b.local_path));
  return files;
}

// ── 3. matching ───────────────────────────────────────────────────────────────
// Returns {method, confidence, rank} or null. Lower rank = stronger.
function matchTierForFile(track, file) {
  const fnameNoExt = file.filename.replace(/\.(mp3|wav|flac|m4a|aac|ogg)$/i, "").toLowerCase();
  const idLower = String(track.track_id).toLowerCase();

  // 1) exact track id present in filename
  if (fnameNoExt.includes(idLower)) return { method: "track_id_in_filename", confidence: "exact", rank: 0 };

  // 2) exact current storage filename / key basename
  if (track.current_storage_key) {
    const keyBase = path.basename(String(track.current_storage_key)).replace(/\.(mp3|wav|flac|m4a|aac|ogg)$/i, "").toLowerCase();
    if (keyBase && fnameNoExt === keyBase) return { method: "storage_filename", confidence: "exact", rank: 0 };
  }

  const tTitle = normalize(track.title);
  const fTitle = normalize(file.title_guess || file.filename);
  if (!tTitle || tTitle !== fTitle) return null;

  // 3) creator username + normalized title
  const cu = normalize(track.creator_username || "");
  const cd = normalize(track.creator_display || "");
  const hay = normalize(file.local_path + " " + (file.creator_guess || ""));
  if ((cu && hay.includes(cu)) || (cd && hay.includes(cd))) return { method: "creator_and_title", confidence: "high", rank: 1 };

  // 4) normalized title (+ duration tolerance when both known)
  if (file.duration_seconds && track.duration_seconds) {
    const within = Math.abs(Number(file.duration_seconds) - Number(track.duration_seconds)) <= 2;
    return { method: "title_and_duration", confidence: within ? "medium" : "low", rank: within ? 2 : 3 };
  }
  return { method: "title_only", confidence: "medium", rank: 2 };
}

function buildMatches(tracks, files, okSet, force) {
  const rows = [];
  const usedFiles = new Set();

  for (const t of tracks) {
    const playable = okSet.has(String(t.track_id).toLowerCase());
    const cands = [];
    for (const f of files) {
      const m = matchTierForFile(t, f);
      if (m) cands.push({ f, ...m });
    }
    cands.sort((a, b) => a.rank - b.rank);
    const bestRank = cands.length ? cands[0].rank : Infinity;
    const best = cands.filter((c) => c.rank === bestRank);

    let action, confidence, matched = null, method = "", notes = "";

    if (playable && !force) {
      action = "skip_already_playable";
      confidence = "exact";
      method = "already_in_r2";
    } else if (best.length === 0) {
      action = "missing_local_file";
      confidence = "manual_required";
      notes = "No local file matched this track.";
    } else if (best.length > 1) {
      action = "ambiguous_match";
      confidence = "manual_required";
      method = best[0].method;
      notes = `Multiple local files matched at confidence=${best[0].confidence}: ${best.map((c) => path.basename(c.f.local_path)).join(" | ")}`;
    } else {
      matched = best[0].f;
      confidence = best[0].confidence;
      method = best[0].method;
      usedFiles.add(matched.local_path);
      if (confidence === "exact" || confidence === "high") {
        action = "upload";
        if (matched.file_size === 0) { action = "manual_review"; notes = "Matched local file is 0 bytes."; }
      } else {
        action = "manual_review";
        notes = `Low/medium confidence (${confidence}); not auto-applied.`;
      }
    }

    const ext = matched ? matched.extension : (t.current_storage_key ? path.extname(String(t.current_storage_key)) : ".mp3");
    const planned = t.current_storage_key && String(t.current_storage_key).trim()
      ? String(t.current_storage_key).trim() // upload to the key the DB already references — no DB write needed
      : `tracks/${t.creator_username || t.creator_id || "unknown"}/${t.track_id}/original${ext}`;

    rows.push({
      track_id: t.track_id,
      title: t.title,
      creator_username: t.creator_username || t.creator_id || "",
      current_storage_key: t.current_storage_key || "",
      matched_local_path: matched ? matched.local_path : "",
      match_method: method,
      confidence,
      file_size: matched ? matched.file_size : "",
      sha256: matched ? matched.sha256 : "",
      duration_seconds: matched ? matched.duration_seconds : "",
      planned_r2_key: action === "skip_already_playable" ? (t.current_storage_key || "") : planned,
      action,
      notes,
      _track: t,
      _matched: matched,
    });
  }

  // Unmatched local files (reported, never auto-uploaded).
  for (const f of files) {
    if (usedFiles.has(f.local_path)) continue;
    rows.push({
      track_id: "", title: "", creator_username: "", current_storage_key: "",
      matched_local_path: f.local_path, match_method: "", confidence: "manual_required",
      file_size: f.file_size, sha256: f.sha256, duration_seconds: f.duration_seconds,
      planned_r2_key: "", action: "unmatched_local_file",
      notes: "Local file did not match any production track.",
    });
  }
  return rows;
}

// ── 5. apply a single planned upload (gated) ──────────────────────────────────
async function applyOne(row, token, auditLog) {
  const t = row._track, f = row._matched;
  const key = row.planned_r2_key;
  const rec = { ts: new Date().toISOString(), trackId: t.track_id, title: t.title, key, localPath: f.local_path, steps: [] };
  try {
    // Pre-check: does the object already exist?
    const before = await storageProbe(cfg, token, key);
    if (before.exists) {
      if (before.length != null && Number(before.length) === Number(f.file_size)) {
        rec.steps.push("object_exists_size_match_skip_upload");
      } else if (!args.force) {
        rec.result = "manual_review"; rec.note = `Existing R2 object differs (r2=${before.length} local=${f.file_size}); use --force to overwrite.`;
        auditLog.push(rec); return rec;
      } else {
        rec.steps.push("object_exists_size_differs_force_overwrite");
        r2Put(cfg.bucket, key, f.local_path); rec.steps.push("uploaded");
      }
    } else {
      r2Put(cfg.bucket, key, f.local_path); rec.steps.push("uploaded");
    }

    // Verify object now exists with matching size.
    const after = await storageProbe(cfg, token, key);
    if (!after.exists) { rec.result = "failed_upload"; rec.note = "Object not found after upload."; auditLog.push(rec); return rec; }
    if (after.length != null && Number(after.length) !== Number(f.file_size)) {
      rec.result = "failed_upload"; rec.note = `Size mismatch after upload (r2=${after.length} local=${f.file_size}).`; auditLog.push(rec); return rec;
    }
    rec.steps.push(`verified_object size=${after.length}`);

    // Update DB only if the row does not already point at this key.
    if (String(t.current_storage_key || "") !== key) {
      const n = await updateAudioKey(cfg, t.track_id, key);
      rec.steps.push(`db_updated rows=${n}`);
    } else {
      rec.steps.push("db_already_correct");
    }

    // Stream verification — the only thing that marks a track truly playable.
    const sv = await streamVerify(cfg, t.track_id);
    rec.stream = sv;
    if (sv.ok) { rec.result = "playable"; }
    else { rec.result = "failed_stream_verification"; rec.note = `Stream returned HTTP ${sv.status} ct=${sv.contentType}`; }
  } catch (e) {
    rec.result = "error"; rec.note = String(e.message || e);
  }
  auditLog.push(rec);
  return rec;
}

// ── final report ──────────────────────────────────────────────────────────────
function writeFinalReport(matchRows, playableBefore, playableAfter, applyRecs) {
  const count = (a) => matchRows.filter((r) => r.action === a).length;
  const stats = {
    total: matchRows.filter((r) => r.track_id).length,
    playable_before: playableBefore.size,
    playable_after: playableAfter.size,
    uploaded: applyRecs.filter((r) => r.steps?.includes("uploaded")).length,
    skipped_already_playable: count("skip_already_playable"),
    db_updated: applyRecs.filter((r) => (r.steps || []).some((s) => s.startsWith("db_updated"))).length,
    manual_review: count("manual_review"),
    missing_local_file: count("missing_local_file"),
    ambiguous_match: count("ambiguous_match"),
    failed_upload: applyRecs.filter((r) => r.result === "failed_upload").length,
    failed_stream: applyRecs.filter((r) => r.result === "failed_stream_verification").length,
  };

  // per-creator
  const byCreator = new Map();
  for (const r of matchRows) {
    if (!r.track_id) continue;
    const cu = r.creator_username || "(unknown)";
    if (!byCreator.has(cu)) byCreator.set(cu, { total: 0, before: 0, after: 0 });
    const c = byCreator.get(cu);
    c.total++;
    if (playableBefore.has(String(r.track_id).toLowerCase())) c.before++;
    if (playableAfter.has(String(r.track_id).toLowerCase())) c.after++;
  }
  const creatorRows = [...byCreator.entries()].map(([creator_username, c]) => ({
    creator_username, total_tracks: c.total, playable_before: c.before, playable_after: c.after, remaining_broken: c.total - c.after,
  })).sort((a, b) => b.remaining_broken - a.remaining_broken || a.creator_username.localeCompare(b.creator_username));

  writeCsv(path.join(OUT, "audio-rehydration-final.csv"),
    ["creator_username", "total_tracks", "playable_before", "playable_after", "remaining_broken"], creatorRows);

  const md = [];
  md.push(`# Audio Rehydration — Final Report`);
  md.push(``);
  md.push(`- Mode: **${MODE}**`);
  md.push(`- Backend: \`${cfg.backend}\``);
  md.push(`- R2 bucket: \`${cfg.bucket}\``);
  md.push(`- Generated: ${new Date().toISOString()}`);
  md.push(``);
  md.push(`## Totals`);
  md.push(``);
  md.push(`| metric | value |`);
  md.push(`| --- | ---: |`);
  md.push(`| total production tracks | ${stats.total} |`);
  md.push(`| playable before | ${stats.playable_before} |`);
  md.push(`| playable after | ${stats.playable_after} |`);
  md.push(`| uploaded | ${stats.uploaded} |`);
  md.push(`| skipped (already playable) | ${stats.skipped_already_playable} |`);
  md.push(`| DB rows updated | ${stats.db_updated} |`);
  md.push(`| manual review | ${stats.manual_review} |`);
  md.push(`| missing local file | ${stats.missing_local_file} |`);
  md.push(`| ambiguous match | ${stats.ambiguous_match} |`);
  md.push(`| failed upload | ${stats.failed_upload} |`);
  md.push(`| failed stream verification | ${stats.failed_stream} |`);
  md.push(``);
  md.push(`## Per-creator before/after`);
  md.push(``);
  md.push(`| creator_username | total_tracks | playable_before | playable_after | remaining_broken |`);
  md.push(`| --- | ---: | ---: | ---: | ---: |`);
  for (const c of creatorRows) md.push(`| ${c.creator_username} | ${c.total_tracks} | ${c.playable_before} | ${c.playable_after} | ${c.remaining_broken} |`);
  md.push(``);
  md.push(`## Reports in this directory`);
  md.push(`- \`local-audio-manifest.csv\` — every local audio file scanned`);
  md.push(`- \`production-track-manifest.csv\` — every production track row + current key + live stream status`);
  md.push(`- \`audio-match-report.csv\` — planned action per track`);
  md.push(`- \`audio-rehydration-final.csv\` — per-creator before/after table`);
  if (applyRecs.length) md.push(`- \`apply-audit-log.jsonl\` — one record per changed/attempted row`);
  fs.writeFileSync(path.join(OUT, "audio-rehydration-final-report.md"), md.join("\n") + "\n", "utf8");
  return stats;
}

// ── main ──────────────────────────────────────────────────────────────────────
async function main() {
  log.step(`Cambrian audio rehydration — mode=${MODE}`);
  log.dim(`  backend=${cfg.backend}  bucket=${cfg.bucket}  output=${OUT}`);

  const roots = (args.localRoots.length ? args.localRoots : defaultLocalRoots());
  if (!roots.length) { log.err("No --local-root provided and ./local-tracks not found."); process.exit(2); }
  log.step(`Scanning local audio: ${roots.join(", ")}`);
  const localFiles = scanLocalAudio(roots);
  writeCsv(path.join(OUT, "local-audio-manifest.csv"),
    ["local_path", "filename", "normalized_filename", "extension", "file_size", "sha256", "duration_seconds", "title_guess", "creator_guess"],
    localFiles);
  log.ok(`local-audio-manifest.csv — ${localFiles.length} files`);

  if (args.scanOnly) { log.ok("scan-only complete."); return; }

  // Backend auth + audit (authoritative current state).
  log.step("Authenticating to backend + fetching audit");
  const token = await backendLogin(cfg);
  const audit = await audioAudit(cfg, token);
  const missingIds = new Set((audit.missing || []).map((m) => String(m.trackId).toLowerCase()));
  log.ok(`audit: total=${audit.totalTracks} ok=${audit.okCount} missing=${audit.missingCount}`);

  // Track rows. Prefer the DB (authoritative AudioUrl + creator join); fall back to
  // backend-proxied audit + catalog where outbound :5432 is blocked.
  let tracks;
  let dbOk = false;
  if (cfg.databaseUrl) {
    log.step("Probing direct DB access");
    dbOk = await dbReachable(cfg);
  }
  if (dbOk) {
    log.step("Reading production track rows from DB");
    tracks = await fetchTracks(cfg);
    log.ok(`tracks (DB): ${tracks.length}`);
  } else {
    log.warn("Direct DB unreachable (or DATABASE_URL unset) — assembling tracks from backend audit + catalog.");
    const catalog = await catalogAll(cfg);
    const map = new Map();
    // Broken tracks (authoritative current key from the audit).
    for (const m of audit.missing || []) {
      const id = String(m.trackId).toLowerCase();
      const meta = catalog.get(id) || {};
      map.set(id, {
        track_id: m.trackId, cambrian_track_id: "", title: m.title || meta.title || "",
        current_storage_key: m.audioUrl || "", cover_art_url: "",
        status: meta.status || "", visibility: meta.visibility || "",
        creator_id: meta.creator_id || "", created_at: meta.created_at || "",
        creator_username: meta.creator_username || "", creator_display: meta.creator_display || "",
      });
    }
    // Already-playable tracks visible in the catalog (current key not exposed by API).
    for (const [id, meta] of catalog) {
      if (map.has(id) || missingIds.has(id)) continue;
      map.set(id, {
        track_id: id, cambrian_track_id: "", title: meta.title,
        current_storage_key: "", cover_art_url: "",
        status: meta.status, visibility: meta.visibility,
        creator_id: meta.creator_id, created_at: meta.created_at,
        creator_username: meta.creator_username, creator_display: meta.creator_display,
      });
    }
    tracks = [...map.values()];
    log.ok(`tracks (audit+catalog): ${tracks.length} (note: hidden already-playable tracks may be unlisted)`);
  }
  const okSet = new Set(tracks.map((t) => String(t.track_id).toLowerCase()).filter((id) => !missingIds.has(id)));

  // production-track-manifest.csv (+ live stream check, sampled or full)
  log.step("Verifying live stream status per track");
  const limited = args.limit ? tracks.slice(0, args.limit) : tracks;
  const prodRows = [];
  for (const t of limited) {
    const playable = okSet.has(String(t.track_id).toLowerCase());
    let streamAvailable = playable;
    prodRows.push({
      track_id: t.track_id, title: t.title, creator_id: t.creator_id, creator_username: t.creator_username || "",
      status: t.status, visibility: t.visibility, current_audio_url: `${cfg.backend}/stream/${t.track_id}/audio`,
      current_storage_key: t.current_storage_key || "", current_bucket: cfg.bucket,
      stream_available: streamAvailable, created_at: t.created_at?.toISOString?.() ?? t.created_at,
    });
  }
  writeCsv(path.join(OUT, "production-track-manifest.csv"),
    ["track_id", "title", "creator_id", "creator_username", "status", "visibility", "current_audio_url", "current_storage_key", "current_bucket", "stream_available", "created_at"],
    prodRows);
  log.ok(`production-track-manifest.csv — ${prodRows.length} tracks`);

  // Matching
  log.step("Matching local files to production tracks");
  const matchRows = buildMatches(tracks, localFiles, okSet, args.force);
  writeCsv(path.join(OUT, "audio-match-report.csv"),
    ["track_id", "title", "creator_username", "current_storage_key", "matched_local_path", "match_method", "confidence", "file_size", "sha256", "duration_seconds", "planned_r2_key", "action", "notes"],
    matchRows);
  const byAction = matchRows.reduce((m, r) => ((m[r.action] = (m[r.action] || 0) + 1), m), {});
  log.ok(`audio-match-report.csv — ${JSON.stringify(byAction)}`);

  const playableBefore = new Set(okSet);
  const playableAfter = new Set(okSet);
  const applyRecs = [];

  const eligible = matchRows.filter((r) => r.action === "upload" && (r.confidence === "exact" || r.confidence === "high"));

  if (args.apply) {
    if (cfg.confirm !== CONFIRM_TOKEN) {
      log.err(`Refusing to apply: set CONFIRM_PRODUCTION_AUDIO_REHYDRATE=${CONFIRM_TOKEN}`);
      process.exit(3);
    }
    if (!cfg.databaseUrl) { log.err("Refusing to apply: DATABASE_URL not set."); process.exit(3); }
    log.warn(`APPLY: ${eligible.length} eligible exact/high-confidence upload(s).`);
    for (const r of eligible) {
      const rec = await applyOne(r, token, applyRecs);
      if (rec.result === "playable") { playableAfter.add(String(r.track_id).toLowerCase()); log.ok(`${r.title} → playable`); }
      else log.warn(`${r.title} → ${rec.result}${rec.note ? " (" + rec.note + ")" : ""}`);
    }
    fs.writeFileSync(path.join(OUT, "apply-audit-log.jsonl"), applyRecs.map((r) => JSON.stringify(r)).join("\n") + "\n", "utf8");
  } else {
    log.warn(`DRY-RUN: would upload ${eligible.length} exact/high-confidence match(es). No writes performed.`);
    for (const r of eligible) log.dim(`  would upload ${path.basename(r.matched_local_path)} -> ${cfg.bucket}/${r.planned_r2_key} (track ${r.title})`);
  }

  const stats = writeFinalReport(matchRows, playableBefore, playableAfter, applyRecs);
  log.step("Final report");
  console.log(JSON.stringify(stats, null, 2));
  log.ok(`Reports written to ${OUT}`);
}

main().catch((e) => { log.err(e.stack || String(e)); process.exit(1); });
