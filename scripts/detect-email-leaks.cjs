#!/usr/bin/env node
// Cambrian Backend — Creator Identity Email Leak Detector
"use strict";
const fs = require("fs");
const path = require("path");
const ROOT = path.resolve(__dirname, "..");
const SRC_DIR = path.join(ROOT, "src");
const SCAN_DIRS = [
  path.join(SRC_DIR, "Cambrian.Api", "Controllers"),
  path.join(SRC_DIR, "Cambrian.Application", "Services"),
  path.join(SRC_DIR, "Cambrian.Application", "DTOs"),
  path.join(SRC_DIR, "Cambrian.Persistence", "Repositories"),
];
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
function rel(filePath) { return path.relative(ROOT, filePath).replace(/\\/g, "/"); }
const violations = [];
function checkNoEmailInArtistField(files) {
  const emailFallbackPattern = /\?\?\s*\w*\.?(Creator|User|Author)\??\.(Email|EmailAddress)/gi;
  const artistEmailPattern = /Artist\s*=\s*[^,;]*\.(Email|EmailAddress)/gi;
  for (const f of files) {
    const src = fs.readFileSync(f, "utf-8");
    const lines = src.split("\n");
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      if (emailFallbackPattern.test(line)) {
        violations.push({ rule: "no-email-in-artist", file: rel(f), line: i + 1, message: "Email used as fallback in artist/creator context: " + line.trim() });
      }
      emailFallbackPattern.lastIndex = 0;
      if (artistEmailPattern.test(line)) {
        violations.push({ rule: "no-email-in-artist", file: rel(f), line: i + 1, message: "Artist field assigned from Email: " + line.trim() });
      }
      artistEmailPattern.lastIndex = 0;
    }
  }
}
function checkNoEmailInPublicCreatorDto(files) {
  for (const f of files) {
    if (!f.includes("PublicCreator") && !f.includes("CreatorResponse")) continue;
    const src = fs.readFileSync(f, "utf-8");
    if (/public\s+string\??\s+Email\s*\{/i.test(src)) {
      violations.push({ rule: "no-email-in-public-dto", file: rel(f), line: null, message: "Public creator DTO exposes Email property" });
    }
  }
}
function checkTrackQueriesUseUuid(files) {
  for (const f of files) {
    const src = fs.readFileSync(f, "utf-8");
    if (/\.Where\([^)]*\.Email\s*==/.test(src) && /Track|track/i.test(src)) {
      violations.push({ rule: "track-query-uses-uuid", file: rel(f), line: null, message: "Track query filters by Email instead of CreatorUuid" });
    }
  }
}
const allFiles = SCAN_DIRS.flatMap(findCsFiles);
checkNoEmailInArtistField(allFiles);
checkNoEmailInPublicCreatorDto(allFiles);
checkTrackQueriesUseUuid(allFiles);
if (violations.length === 0) {
  console.log("✓ Creator identity email-leak check passed (0 violations)");
  process.exit(0);
} else {
  console.error(violations.length + " email-leak violation(s) found:\n");
  for (const v of violations) {
    const loc = v.line ? v.file + ":" + v.line : v.file;
    console.error("  [" + v.rule + "] " + loc);
    console.error("    " + v.message + "\n");
  }
  process.exit(1);
}
