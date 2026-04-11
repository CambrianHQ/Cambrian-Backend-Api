# Synthetic Asset Pipeline

Tools for generating demo content for Cambrian staging and QA environments.

## Structure

```
seed/
├── audio/
│   ├── presets/          # Genre config (BPM ranges, durations, moods)
│   │   ├── ambient.json
│   │   ├── lofi.json
│   │   ├── edm.json
│   │   ├── cinematic.json
│   │   └── trap.json
│   └── generated/        # Output WAV files (git-ignored)
├── metadata/
│   ├── creators.json     # Generated creator profiles
│   ├── tracks.json       # Generated track metadata
│   └── generated-tracks.json  # Audio generator output index
└── scripts/
    ├── generate-audio-assets.py   # Procedural WAV generator (Python)
    └── build-demo-catalog.mjs     # Full catalog metadata builder (Node)
```

## Quick Start

### 1. Build catalog metadata

```bash
node seed/scripts/build-demo-catalog.mjs
# Outputs: seed/metadata/creators.json, seed/metadata/tracks.json
```

Options:
```bash
node seed/scripts/build-demo-catalog.mjs --premium 50 --filler 500
```

### 2. Generate audio files

```bash
pip install numpy
python seed/scripts/generate-audio-assets.py --all --count 5
# Outputs: seed/audio/generated/<genre>/*.wav
```

Single genre:
```bash
python seed/scripts/generate-audio-assets.py --preset ambient --count 10
```

## Catalog Composition

| Genre | Creators | Structure | Tracks |
|-------|----------|-----------|--------|
| Ambient | 5 | 3 albums × 6 tracks | 90 |
| Lo-fi | 5 | 2 albums × 8 tracks | 80 |
| EDM | 4 | 2 albums × 5 tracks | 40 |
| Cinematic | 3 | 2 albums × 6 tracks | 36 |
| Trap | 4 | Singles (16 each) | 64 |

**Total: ~310 tracks, 21 creators**

## Strategy

- **Premium tracks** (first N): Higher quality, curated for homepage / feature surfaces
- **Filler tracks** (remainder): Procedurally generated for scale testing (search, pagination, analytics)
- All demo assets are tagged `demoOnly: true` — never mixed with real catalog

## Audio Generation

The Python generator synthesizes genre-appropriate audio procedurally:

| Genre | Audio characteristics |
|-------|---------------------|
| Ambient | Evolving pads, soft noise beds, slow LFO modulation |
| Lo-fi | Mellow chord stabs, vinyl crackle, low-pass filtering |
| EDM | Four-on-the-floor kick, supersaw pads, sidechain compression |
| Cinematic | Piano ostinato, string pads, sub bass booms |
| Trap | 808 bass, sparse hi-hats, dark chord pads |

All files are deterministic (seeded RNG) for reproducible builds.
