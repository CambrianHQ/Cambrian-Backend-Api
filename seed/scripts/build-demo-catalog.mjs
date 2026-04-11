#!/usr/bin/env node

/**
 * Build a complete demo catalog (creators, albums, tracks) for Cambrian staging.
 *
 * Reads genre presets, generates deterministic metadata for a realistic-looking
 * catalog, and outputs JSON files that can be imported by the .NET seeder or
 * used alongside the Python audio generator.
 *
 * Usage:
 *   node seed/scripts/build-demo-catalog.mjs
 *   node seed/scripts/build-demo-catalog.mjs --premium 50 --filler 500
 */

import fs from "fs";
import path from "path";

const ROOT = path.resolve(import.meta.dirname, "..", "..");
const SEED_ROOT = path.join(ROOT, "seed");
const METADATA_DIR = path.join(SEED_ROOT, "metadata");

// ── Deterministic pseudo-random (seeded) ──────────────────────────

class SeededRNG {
  constructor(seed = 42) {
    this.state = seed;
  }
  next() {
    this.state = (this.state * 1664525 + 1013904223) & 0xffffffff;
    return (this.state >>> 0) / 0xffffffff;
  }
  int(min, max) {
    return Math.floor(this.next() * (max - min + 1)) + min;
  }
  pick(arr) {
    return arr[this.int(0, arr.length - 1)];
  }
  shuffle(arr) {
    const a = [...arr];
    for (let i = a.length - 1; i > 0; i--) {
      const j = this.int(0, i);
      [a[i], a[j]] = [a[j], a[i]];
    }
    return a;
  }
}

// ── Name generators ───────────────────────────────────────────────

const CREATOR_PREFIXES = [
  "Echo", "Neon", "Drift", "Pulse", "Velvet", "Nova", "Sonic", "Aura",
  "Flux", "Ember", "Haze", "Prism", "Cloud", "Orbit", "Shade", "Crystal",
  "Luna", "Zen", "Void", "Pixel", "Cyan", "Frost", "Blaze", "Terra",
];
const CREATOR_SUFFIXES = [
  "beats", "wave", "lab", "studio", "sound", "keys", "tone", "mix",
  "fx", "audio", "pulse", "synth", "craft", "zone", "verse", "flow",
];

const TRACK_ADJECTIVES = [
  "Starlight", "Midnight", "Golden", "Velvet", "Crystal", "Silent",
  "Distant", "Frozen", "Eternal", "Lunar", "Neon", "Fading",
  "Drifting", "Hollow", "Radiant", "Shattered", "Cosmic", "Gentle",
  "Hidden", "Phantom", "Rising", "Falling", "Ancient", "Electric",
];
const TRACK_NOUNS = [
  "Drift", "Pulse", "Echo", "Horizon", "Waves", "Rain", "Embers",
  "Skies", "Fields", "Streets", "Dreams", "Shadows", "Lights",
  "Currents", "Depths", "Passage", "Signal", "Bloom", "Requiem",
  "Reverie", "Cascade", "Mirage", "Solace", "Whisper",
];

function genCreatorName(rng) {
  return (rng.pick(CREATOR_PREFIXES) + rng.pick(CREATOR_SUFFIXES)).toLowerCase();
}

function genTrackTitle(rng) {
  return `${rng.pick(TRACK_ADJECTIVES)} ${rng.pick(TRACK_NOUNS)}`;
}

// ── Catalog definition ────────────────────────────────────────────

const CATALOG_SPEC = [
  { genre: "Ambient", creatorsCount: 5, albumsPerCreator: 3, tracksPerAlbum: 6, durRange: [120, 300], bpmRange: [60, 85] },
  { genre: "Lo-fi", creatorsCount: 5, albumsPerCreator: 2, tracksPerAlbum: 8, durRange: [90, 180], bpmRange: [70, 95] },
  { genre: "EDM", creatorsCount: 4, albumsPerCreator: 2, tracksPerAlbum: 5, durRange: [150, 270], bpmRange: [120, 140] },
  { genre: "Cinematic", creatorsCount: 3, albumsPerCreator: 2, tracksPerAlbum: 6, durRange: [120, 300], bpmRange: [70, 100] },
  { genre: "Trap", creatorsCount: 4, albumsPerCreator: 0, singlesCount: 16, durRange: [120, 210], bpmRange: [130, 160] },
];

const MOODS = {
  "Ambient": ["ethereal", "calm", "cinematic", "meditative", "dreamy"],
  "Lo-fi": ["chill", "mellow", "nostalgic", "relaxed", "warm"],
  "EDM": ["energetic", "uplifting", "intense", "euphoric", "driving"],
  "Cinematic": ["epic", "dramatic", "hopeful", "tense", "reflective"],
  "Trap": ["dark", "aggressive", "moody", "hype", "atmospheric"],
};

const KEYS = ["C major", "D minor", "E minor", "F major", "G major", "A minor", "Bb major", "Eb minor"];

// ── Build ─────────────────────────────────────────────────────────

function buildCatalog({ premiumCount, fillerCount }) {
  const rng = new SeededRNG(2026);
  const creators = [];
  const tracks = [];
  let trackNum = 0;

  for (const spec of CATALOG_SPEC) {
    for (let c = 0; c < spec.creatorsCount; c++) {
      const creatorSlug = genCreatorName(rng);
      const creator = {
        slug: creatorSlug,
        displayName: creatorSlug.charAt(0).toUpperCase() + creatorSlug.slice(1),
        genre: spec.genre,
        bio: `Demo creator producing ${spec.genre.toLowerCase()} music.`,
        demoOnly: true,
      };
      creators.push(creator);

      if (spec.albumsPerCreator > 0) {
        for (let a = 0; a < spec.albumsPerCreator; a++) {
          const albumTitle = genTrackTitle(rng);
          for (let t = 0; t < spec.tracksPerAlbum; t++) {
            trackNum++;
            const isPremium = trackNum <= premiumCount;
            tracks.push({
              id: `demo-track-${String(trackNum).padStart(4, "0")}`,
              title: genTrackTitle(rng),
              creatorSlug,
              albumTitle,
              genre: spec.genre,
              mood: rng.pick(MOODS[spec.genre] || ["neutral"]),
              bpm: rng.int(spec.bpmRange[0], spec.bpmRange[1]),
              key: rng.pick(KEYS),
              durationSeconds: rng.int(spec.durRange[0], spec.durRange[1]),
              nonExclusivePriceCents: rng.pick([1999, 2499, 2999, 3499]),
              exclusivePriceCents: rng.pick([24999, 49999, 74999, 99999]),
              copyrightBuyoutPriceCents: rng.pick([99999, 149999, 199999]),
              tier: isPremium ? "premium" : "filler",
              demoOnly: true,
              sourceType: isPremium ? "curated" : "procedural",
            });
          }
        }
      } else {
        // Singles-heavy (Trap)
        const singlesCount = spec.singlesCount || 12;
        for (let t = 0; t < singlesCount; t++) {
          trackNum++;
          const isPremium = trackNum <= premiumCount;
          tracks.push({
            id: `demo-track-${String(trackNum).padStart(4, "0")}`,
            title: genTrackTitle(rng),
            creatorSlug,
            albumTitle: null,
            genre: spec.genre,
            mood: rng.pick(MOODS[spec.genre] || ["neutral"]),
            bpm: rng.int(spec.bpmRange[0], spec.bpmRange[1]),
            key: rng.pick(KEYS),
            durationSeconds: rng.int(spec.durRange[0], spec.durRange[1]),
            nonExclusivePriceCents: rng.pick([1999, 2499, 2999, 3499]),
            exclusivePriceCents: rng.pick([24999, 49999, 74999, 99999]),
            copyrightBuyoutPriceCents: rng.pick([99999, 149999, 199999]),
            tier: isPremium ? "premium" : "filler",
            demoOnly: true,
            sourceType: isPremium ? "curated" : "procedural",
          });
        }
      }
    }
  }

  return { creators, tracks };
}

// ── Main ──────────────────────────────────────────────────────────

function main() {
  const args = process.argv.slice(2);
  const getArg = (name, def) => {
    const idx = args.indexOf(name);
    return idx !== -1 && args[idx + 1] ? parseInt(args[idx + 1], 10) : def;
  };

  const premiumCount = getArg("--premium", 50);
  const fillerCount = getArg("--filler", 500);

  fs.mkdirSync(METADATA_DIR, { recursive: true });

  console.log(`Building demo catalog (premium=${premiumCount}, filler=${fillerCount})…`);
  const { creators, tracks } = buildCatalog({ premiumCount, fillerCount });

  const creatorsPath = path.join(METADATA_DIR, "creators.json");
  const tracksPath = path.join(METADATA_DIR, "tracks.json");

  fs.writeFileSync(creatorsPath, JSON.stringify(creators, null, 2));
  fs.writeFileSync(tracksPath, JSON.stringify(tracks, null, 2));

  console.log(`✓ ${creators.length} creators → ${creatorsPath}`);
  console.log(`✓ ${tracks.length} tracks → ${tracksPath}`);
  console.log(`  Premium: ${tracks.filter((t) => t.tier === "premium").length}`);
  console.log(`  Filler:  ${tracks.filter((t) => t.tier === "filler").length}`);

  // Summary by genre
  const byGenre = {};
  for (const t of tracks) {
    byGenre[t.genre] = (byGenre[t.genre] || 0) + 1;
  }
  console.log("\nBy genre:");
  for (const [g, n] of Object.entries(byGenre)) {
    console.log(`  ${g}: ${n} tracks`);
  }
}

main();
