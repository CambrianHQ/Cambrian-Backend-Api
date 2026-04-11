"""
Procedural audio generator for Cambrian staging / demo environments.

Generates synthetic WAV files per genre preset, then encodes to MP3.
Each preset produces genre-appropriate placeholder audio (pads, drones,
simple patterns) suitable for testing uploads, streaming, waveforms,
and catalog population.

Usage:
    pip install numpy soundfile
    python seed/scripts/generate-audio-assets.py
    python seed/scripts/generate-audio-assets.py --preset ambient --count 10
    python seed/scripts/generate-audio-assets.py --all
"""

import argparse
import json
import math
import os
import struct
import sys
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent.parent
PRESETS_DIR = ROOT / "audio" / "presets"
OUTPUT_DIR = ROOT / "audio" / "generated"
METADATA_DIR = ROOT / "metadata"

SAMPLE_RATE = 44100


# ── Synthesis helpers ──────────────────────────────────────────────

def sine_wave(freq, duration, sr=SAMPLE_RATE, amplitude=0.5):
    t = np.linspace(0, duration, int(sr * duration), endpoint=False)
    return amplitude * np.sin(2 * np.pi * freq * t)


def noise(duration, sr=SAMPLE_RATE, amplitude=0.1):
    return amplitude * np.random.uniform(-1, 1, int(sr * duration))


def fade(signal, fade_in=0.05, fade_out=0.1, sr=SAMPLE_RATE):
    n_in = int(sr * fade_in)
    n_out = int(sr * fade_out)
    signal[:n_in] *= np.linspace(0, 1, n_in)
    signal[-n_out:] *= np.linspace(1, 0, n_out)
    return signal


def low_pass(signal, cutoff=2000, sr=SAMPLE_RATE):
    """Simple rolling-average low-pass approximation."""
    n = max(1, int(sr / cutoff / 2))
    kernel = np.ones(n) / n
    return np.convolve(signal, kernel, mode='same')


def chord(freqs, duration, sr=SAMPLE_RATE, amplitude=0.3):
    out = np.zeros(int(sr * duration))
    for f in freqs:
        out += sine_wave(f, duration, sr, amplitude / len(freqs))
    return out


# ── Genre generators ──────────────────────────────────────────────

def generate_ambient(duration, bpm=72):
    """Long evolving pad with soft noise bed."""
    root = 220.0  # A3
    pad = chord([root, root * 1.5, root * 2], duration, amplitude=0.35)
    # slow LFO amplitude modulation
    t = np.linspace(0, duration, len(pad), endpoint=False)
    lfo = 0.5 + 0.5 * np.sin(2 * np.pi * 0.05 * t)
    pad *= lfo
    bed = noise(duration, amplitude=0.04)
    bed = low_pass(bed, cutoff=800)
    out = pad + bed
    return fade(out, fade_in=2.0, fade_out=3.0)


def generate_lofi(duration, bpm=85):
    """Mellow keys with vinyl crackle and soft kick loop."""
    sr = SAMPLE_RATE
    out = np.zeros(int(sr * duration))
    beat_len = 60.0 / bpm
    # simple chord stabs on beats
    notes = [261.6, 329.6, 392.0]  # C4-E4-G4
    for i in range(int(duration / beat_len)):
        start = int(i * beat_len * sr)
        stab = chord(notes, min(beat_len * 0.8, 0.6), amplitude=0.25)
        stab = fade(stab, fade_in=0.01, fade_out=0.15)
        stab = low_pass(stab, cutoff=3000)
        end = min(start + len(stab), len(out))
        out[start:end] += stab[:end - start]
    # vinyl crackle
    crackle = noise(duration, amplitude=0.015)
    crackle = low_pass(crackle, cutoff=6000)
    out += crackle
    return fade(out, fade_in=0.5, fade_out=1.0)


def generate_edm(duration, bpm=128):
    """Four-on-the-floor kick with bright supersaw approximation."""
    sr = SAMPLE_RATE
    out = np.zeros(int(sr * duration))
    beat_len = 60.0 / bpm
    # kick
    for i in range(int(duration / beat_len)):
        start = int(i * beat_len * sr)
        kick_dur = 0.15
        t = np.linspace(0, kick_dur, int(sr * kick_dur), endpoint=False)
        freq_sweep = 150 * np.exp(-t * 30) + 40
        kick = 0.6 * np.sin(2 * np.pi * np.cumsum(freq_sweep) / sr)
        kick = fade(kick, fade_in=0.001, fade_out=0.08)
        end = min(start + len(kick), len(out))
        out[start:end] += kick[:end - start]
    # supersaw-ish pad
    root = 440.0
    detunes = [0.98, 0.99, 1.0, 1.01, 1.02]
    pad = np.zeros(int(sr * duration))
    for d in detunes:
        pad += sine_wave(root * d, duration, amplitude=0.08)
    t_full = np.linspace(0, duration, len(pad), endpoint=False)
    sidechain = np.ones(len(pad))
    for i in range(int(duration / beat_len)):
        s = int(i * beat_len * sr)
        e = min(s + int(0.1 * sr), len(pad))
        ramp = np.linspace(0.2, 1.0, e - s)
        sidechain[s:e] = ramp
    pad *= sidechain
    out += pad
    return fade(out, fade_in=0.1, fade_out=0.5)


def generate_cinematic(duration, bpm=90):
    """Piano ostinato with string-like pad and low boom."""
    sr = SAMPLE_RATE
    out = np.zeros(int(sr * duration))
    # string pad
    strings = chord([196.0, 246.9, 293.7, 392.0], duration, amplitude=0.2)
    t = np.linspace(0, duration, len(strings), endpoint=False)
    strings *= 0.5 + 0.5 * np.sin(2 * np.pi * 0.03 * t)
    out += strings
    # piano-like hits
    beat_len = 60.0 / bpm
    piano_notes = [523.3, 587.3, 659.3, 523.3, 493.9, 440.0]
    for i in range(int(duration / beat_len)):
        start = int(i * beat_len * sr)
        note_freq = piano_notes[i % len(piano_notes)]
        note_dur = min(0.4, beat_len * 0.6)
        t_n = np.linspace(0, note_dur, int(sr * note_dur), endpoint=False)
        hit = 0.3 * np.sin(2 * np.pi * note_freq * t_n) * np.exp(-t_n * 5)
        end = min(start + len(hit), len(out))
        out[start:end] += hit[:end - start]
    # sub boom at start
    boom_dur = 2.0
    t_b = np.linspace(0, boom_dur, int(sr * boom_dur), endpoint=False)
    boom = 0.4 * np.sin(2 * np.pi * 40 * t_b) * np.exp(-t_b * 2)
    out[:len(boom)] += boom
    return fade(out, fade_in=1.0, fade_out=2.0)


def generate_trap(duration, bpm=140):
    """808-style bass with sparse hi-hats and dark keys."""
    sr = SAMPLE_RATE
    out = np.zeros(int(sr * duration))
    beat_len = 60.0 / bpm
    # 808 bass on every other beat
    for i in range(0, int(duration / beat_len), 2):
        start = int(i * beat_len * sr)
        bass_dur = beat_len * 1.5
        t_b = np.linspace(0, bass_dur, int(sr * bass_dur), endpoint=False)
        bass = 0.5 * np.sin(2 * np.pi * 55 * t_b) * np.exp(-t_b * 1.5)
        end = min(start + len(bass), len(out))
        out[start:end] += bass[:end - start]
    # hi-hats
    hat_pattern = [1, 0, 1, 1, 0, 1, 0, 1]
    sub_beat = beat_len / 2
    for i in range(int(duration / sub_beat)):
        if hat_pattern[i % len(hat_pattern)]:
            start = int(i * sub_beat * sr)
            hat = noise(0.03, amplitude=0.15)
            end = min(start + len(hat), len(out))
            out[start:end] += hat[:end - start]
    # dark pad
    pad = chord([130.8, 155.6, 196.0], duration, amplitude=0.12)
    pad = low_pass(pad, cutoff=1500)
    out += pad
    return fade(out, fade_in=0.2, fade_out=0.5)


GENERATORS = {
    "ambient": generate_ambient,
    "lofi": generate_lofi,
    "edm": generate_edm,
    "cinematic": generate_cinematic,
    "trap": generate_trap,
}


# ── WAV writer (no external dependency for writing) ───────────────

def write_wav(path, data, sr=SAMPLE_RATE):
    """Write 16-bit mono WAV."""
    data = np.clip(data, -1, 1)
    pcm = (data * 32767).astype(np.int16)
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "wb") as f:
        n_samples = len(pcm)
        data_size = n_samples * 2
        f.write(b"RIFF")
        f.write(struct.pack("<I", 36 + data_size))
        f.write(b"WAVE")
        f.write(b"fmt ")
        f.write(struct.pack("<IHHIIHH", 16, 1, 1, sr, sr * 2, 2, 16))
        f.write(b"data")
        f.write(struct.pack("<I", data_size))
        f.write(pcm.tobytes())


# ── Main ──────────────────────────────────────────────────────────

def load_preset(genre):
    preset_file = PRESETS_DIR / f"{genre}.json"
    if preset_file.exists():
        with open(preset_file) as f:
            return json.load(f)
    # Default preset
    return {
        "genre": genre,
        "bpm_range": [80, 120],
        "duration_range": [120, 240],
        "moods": ["calm", "neutral"],
    }


def generate_for_genre(genre, count, seed=42):
    rng = np.random.default_rng(seed)
    preset = load_preset(genre)
    gen_fn = GENERATORS.get(genre)
    if not gen_fn:
        print(f"No generator for genre '{genre}', skipping.")
        return []

    bpm_lo, bpm_hi = preset.get("bpm_range", [80, 120])
    dur_lo, dur_hi = preset.get("duration_range", [120, 240])
    moods = preset.get("moods", ["neutral"])

    out_dir = OUTPUT_DIR / genre
    tracks = []

    for i in range(count):
        bpm = int(rng.integers(bpm_lo, bpm_hi + 1))
        duration = int(rng.integers(dur_lo, dur_hi + 1))
        mood = moods[i % len(moods)]
        track_id = f"demo-{genre}-{i+1:03d}"
        filename = f"{track_id}.wav"
        filepath = out_dir / filename

        print(f"  Generating {filepath.name}  ({duration}s, {bpm} BPM, {mood})")
        audio = gen_fn(duration, bpm)
        write_wav(filepath, audio)

        tracks.append({
            "id": track_id,
            "genre": genre,
            "bpm": bpm,
            "durationSeconds": duration,
            "mood": mood,
            "audioPath": str(filepath.relative_to(ROOT)),
            "sourceType": "procedural",
            "demoOnly": True,
        })

    return tracks


def main():
    parser = argparse.ArgumentParser(description="Generate synthetic audio for Cambrian staging")
    parser.add_argument("--preset", choices=list(GENERATORS.keys()), help="Generate for a single genre")
    parser.add_argument("--count", type=int, default=5, help="Tracks per genre (default: 5)")
    parser.add_argument("--all", action="store_true", help="Generate for all genres")
    parser.add_argument("--seed", type=int, default=42, help="Random seed for reproducibility")
    args = parser.parse_args()

    if not args.preset and not args.all:
        parser.print_help()
        sys.exit(1)

    genres = list(GENERATORS.keys()) if args.all else [args.preset]
    all_tracks = []

    for genre in genres:
        print(f"\n[{genre.upper()}]")
        tracks = generate_for_genre(genre, args.count, seed=args.seed)
        all_tracks.extend(tracks)

    # Write metadata index
    METADATA_DIR.mkdir(parents=True, exist_ok=True)
    meta_path = METADATA_DIR / "generated-tracks.json"
    with open(meta_path, "w") as f:
        json.dump(all_tracks, f, indent=2)
    print(f"\nMetadata written to {meta_path}")
    print(f"Total tracks generated: {len(all_tracks)}")


if __name__ == "__main__":
    main()
