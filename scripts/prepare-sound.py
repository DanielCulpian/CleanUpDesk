"""Generate an original short, low-amplitude notification with gentle attack and decay."""
import math
import struct
import wave
from pathlib import Path

target = Path(__file__).resolve().parents[1] / 'src/CleanUpDesk.App/Assets/SearchComplete.wav'
target.parent.mkdir(parents=True, exist_ok=True)
rate, duration = 44100, 0.42
samples = bytearray()
for i in range(round(rate * duration)):
    t = i / rate
    attack = min(1, t / 0.025)
    envelope = attack * math.exp(-9 * t) * min(1, (duration - t) / 0.05)
    tone = math.sin(2 * math.pi * 660 * t) + 0.35 * math.sin(2 * math.pi * 880 * t)
    samples.extend(struct.pack('<h', round(32767 * 0.12 * envelope * tone)))
with wave.open(str(target), 'wb') as output:
    output.setparams((1, 2, rate, 0, 'NONE', 'not compressed'))
    output.writeframes(samples)
print('Notification generated: 0.42 s, mono, 44.1 kHz, limited amplitude.')
