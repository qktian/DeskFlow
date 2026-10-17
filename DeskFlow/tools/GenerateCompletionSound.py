"""Generate the replaceable default mission-complete WAV using only Python's stdlib."""

import math
import struct
import wave
from pathlib import Path

RATE = 44100
DURATION = 1.1
DESTINATION = Path(__file__).resolve().parents[1] / "DeskFlow.App" / "Sounds" / "mission-completed.wav"


def note(t: float, start: float, frequency: float, length: float = 0.52) -> float:
    age = t - start
    if age < 0 or age > length:
        return 0.0
    attack = min(1.0, age / 0.012)
    release = (1 - age / length) ** 1.7
    phase = 2 * math.pi * frequency * age
    return attack * release * (math.sin(phase) + 0.22 * math.sin(2 * phase))


samples = []
for index in range(int(RATE * DURATION)):
    t = index / RATE
    value = (
        0.24 * note(t, 0.00, 523.25)
        + 0.24 * note(t, 0.11, 659.25)
        + 0.25 * note(t, 0.22, 783.99)
        + 0.31 * note(t, 0.34, 1046.50, 0.68)
    )
    # A brief rising electronic sweep gives the chime a game-like finish.
    if 0.34 <= t < 0.68:
        age = t - 0.34
        sweep_phase = 2 * math.pi * (160 * age + 900 * age * age)
        value += 0.055 * math.sin(sweep_phase) * (1 - age / 0.34) ** 2
    fade = min(1.0, (DURATION - t) / 0.12)
    samples.append(max(-1.0, min(1.0, value * fade)))

DESTINATION.parent.mkdir(parents=True, exist_ok=True)
with wave.open(str(DESTINATION), "wb") as output:
    output.setnchannels(1)
    output.setsampwidth(2)
    output.setframerate(RATE)
    output.writeframes(b"".join(struct.pack("<h", round(value * 24500)) for value in samples))

print(DESTINATION)
