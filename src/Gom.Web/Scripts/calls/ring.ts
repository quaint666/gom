/**
 * The call's sounds, made in the page (Web Audio): no sound file to ship. A browser may hold sound back until the person has touched
 * the page; then nothing is heard and the ringing screen is still there (docs/voice-calls.md, 2b).
 */
export interface Ring { incoming(): void; outgoing(): void; stop(): void }

/** One burst of a pattern: tones (Hz) played together, from `at` seconds into the period, for `length` seconds. */
interface Burst { tones: number[]; at: number; length: number }

/** Ringing here: two short rising notes, twice, every 3 s. */
const INCOMING: { bursts: Burst[]; every: number; volume: number } = {
  bursts: [{ tones: [659], at: 0, length: 0.18 }, { tones: [880], at: 0.2, length: 0.25 }, { tones: [659], at: 0.6, length: 0.18 }, { tones: [880], at: 0.8, length: 0.25 }],
  every: 3, volume: 0.25,
};
/** Ringing on the other side, as a phone line sounds it: 425 Hz for 1 s every 4 s, quietly. */
const OUTGOING: { bursts: Burst[]; every: number; volume: number } = { bursts: [{ tones: [425], at: 0, length: 1 }], every: 4, volume: 0.08 };

export function createRing(make: () => AudioContext = () => new AudioContext()): Ring {
  let ctx: AudioContext | null = null;
  let timer: number | null = null;

  function burst(c: AudioContext, b: Burst, start: number, volume: number): void {
    const gain = c.createGain();
    gain.connect(c.destination);
    const from = start + b.at, to = from + b.length;
    gain.gain.setValueAtTime(0, from);
    gain.gain.linearRampToValueAtTime(volume, from + 0.02);
    gain.gain.setValueAtTime(volume, to - 0.03);
    gain.gain.linearRampToValueAtTime(0, to);
    for (const hz of b.tones) {
      const osc = c.createOscillator();
      osc.frequency.value = hz;
      osc.connect(gain);
      osc.start(from);
      osc.stop(to);
    }
  }

  function play(pattern: typeof INCOMING): void {
    stop();
    try {
      ctx = make();
      void ctx.resume().catch(() => undefined);
    } catch { ctx = null; return; }
    const c = ctx;
    const round = () => { for (const b of pattern.bursts) burst(c, b, c.currentTime + 0.05, pattern.volume); };
    round();
    timer = window.setInterval(round, pattern.every * 1000);
  }

  function stop(): void {
    if (timer !== null) { window.clearInterval(timer); timer = null; }
    const c = ctx;
    ctx = null;
    void c?.close().catch(() => undefined);
  }

  return { incoming: () => play(INCOMING), outgoing: () => play(OUTGOING), stop };
}
