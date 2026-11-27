import { describe, expect, it } from 'vitest';
import { VOICE_RATE, VOICE_SECONDS, voiceStamp, wav } from '../Scripts/voice.js';

describe('voice messages', () => {
  it('lasts at most five minutes, at 16,000 samples a second', () => {
    expect(VOICE_SECONDS).toBe(300);
    expect(VOICE_RATE).toBe(16_000);
  });

  it('writes one channel as a 16-bit PCM WAV that the server takes as sound', async () => {
    const file = wav(new Float32Array([0, 1, -1, 2, -2, 0.5]), 16_000);
    expect(file.type).toBe('audio/wav');
    const v = new DataView(await file.arrayBuffer());
    const ascii = (at: number, n: number) => String.fromCharCode(...new Uint8Array(v.buffer, at, n));
    expect(v.byteLength).toBe(44 + 12);
    expect(ascii(0, 4)).toBe('RIFF');
    expect(v.getUint32(4, true)).toBe(36 + 12);
    expect(ascii(8, 4)).toBe('WAVE');
    expect(ascii(12, 4)).toBe('fmt ');
    expect(v.getUint16(20, true)).toBe(1);        // PCM
    expect(v.getUint16(22, true)).toBe(1);        // one channel
    expect(v.getUint32(24, true)).toBe(16_000);
    expect(v.getUint32(28, true)).toBe(32_000);   // bytes a second
    expect(v.getUint16(32, true)).toBe(2);
    expect(v.getUint16(34, true)).toBe(16);
    expect(ascii(36, 4)).toBe('data');
    expect(v.getUint32(40, true)).toBe(12);
    // Out of range is held at the ends, not wrapped round.
    expect([0, 1, 2, 3, 4, 5].map((i) => v.getInt16(44 + i * 2, true))).toEqual([0, 32767, -32768, 32767, -32768, 16384]);
  });

  it('names a recording by when it was made, in this device’s time, with no colon', () => {
    expect(voiceStamp(new Date(2026, 10, 8, 9, 5, 3))).toBe('2026-11-08 09.05.03');
  });
});
