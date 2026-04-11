import random
from pathlib import Path

import numpy as np
import soundfile as sf
from faker import Faker

fake = Faker()

OUTPUT_DIR = Path("seed/audio/generated")
GENRES = {
    "ambient": 72,
    "lofi": 80,
    "edm": 128,
    "cinematic": 90,
    "hiphop": 95,
}

SAMPLE_RATE = 44100


def ensure_dirs():
    for genre in GENRES.keys():
        (OUTPUT_DIR / genre).mkdir(parents=True, exist_ok=True)


def generate_pad(duration, freq=220):
    t = np.linspace(0, duration, int(SAMPLE_RATE * duration), False)
    wave = np.sin(2 * np.pi * freq * t)
    envelope = np.exp(-t / duration)
    return (wave * envelope * 0.5).astype(np.float32)


def create_track(genre, duration):
    base_freq = random.choice([110, 220, 330])
    audio = generate_pad(duration, base_freq)

    # Add subtle noise for realism
    noise = np.random.normal(0, 0.005, len(audio))
    audio += noise

    return audio


def save_track(genre, index):
    duration = random.randint(90, 240)
    audio = create_track(genre, duration)

    title = fake.word().capitalize() + " " + fake.word().capitalize()
    file_path = OUTPUT_DIR / genre / f"{index:03d}_{title}.wav"

    sf.write(file_path, audio, SAMPLE_RATE)
    print(f"Generated: {file_path}")


def main():
    ensure_dirs()
    for genre in GENRES.keys():
        for i in range(10):
            save_track(genre, i)


if __name__ == "__main__":
    main()
