/**
 * A voice message: recorded with the browser's own recorder, then made into a WAV file (one channel, 16 kHz), which every browser plays
 * with its length known and the server already takes as sound (`Uploads/Audio.cs`). A recorder's own file (WebM or a fragmented MP4)
 * would be smaller, but browsers show no length for it and cannot seek in it; a minute of WAV is about 1.9 MB.
 */

/** The longest voice message, in seconds: recording stops and sends there. */
export const VOICE_SECONDS = 300;

/** Samples a second: enough for speech, as a phone call carries it. */
export const VOICE_RATE = 16_000;

/** A recording under way: stop it for its WAV (null if nothing came of it), or throw it away. */
export interface Recording {
  stop(): Promise<Blob | null>;
  cancel(): void;
}

/** Starts recording from the microphone; "denied" when the person or the device said no, "unsupported" when this browser cannot. */
export async function recordVoice(): Promise<Recording | 'denied' | 'unsupported'> {
  if (typeof MediaRecorder === 'undefined' || navigator.mediaDevices?.getUserMedia === undefined) return 'unsupported';
  let stream: MediaStream;
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } });
  } catch (e) {
    return e instanceof DOMException && (e.name === 'NotAllowedError' || e.name === 'SecurityError') ? 'denied' : 'unsupported';
  }
  const chunks: Blob[] = [];
  const recorder = new MediaRecorder(stream);
  recorder.ondataavailable = (e) => { if (e.data.size > 0) chunks.push(e.data); };
  const release = () => { for (const track of stream.getTracks()) track.stop(); };
  recorder.start(1000);
  return {
    stop: () => new Promise<Blob | null>((resolve) => {
      recorder.onstop = () => {
        release();
        void toWav(new Blob(chunks, { type: recorder.mimeType })).then(resolve, () => resolve(null));
      };
      recorder.stop();
    }),
    cancel: () => { recorder.onstop = null; if (recorder.state !== 'inactive') recorder.stop(); release(); },
  };
}

/** The recorder's file, decoded at [VOICE_RATE] (the browser resamples it) and mixed down to one channel. */
async function toWav(recorded: Blob): Promise<Blob | null> {
  if (recorded.size === 0) return null;
  const decoded = await new OfflineAudioContext(1, 1, VOICE_RATE).decodeAudioData(await recorded.arrayBuffer());
  const mono = new Float32Array(decoded.length);
  for (let c = 0; c < decoded.numberOfChannels; c++) {
    const channel = decoded.getChannelData(c);
    for (let i = 0; i < mono.length; i++) mono[i]! += channel[i]! / decoded.numberOfChannels;
  }
  return mono.length === 0 ? null : wav(mono, decoded.sampleRate);
}

/** When a voice message was recorded, for its file name, in this device's time: "2026-11-28 12.30.05" (a colon is no good in a file name). */
export function voiceStamp(at: Date): string {
  const two = (n: number) => String(n).padStart(2, '0');
  return `${at.getFullYear()}-${two(at.getMonth() + 1)}-${two(at.getDate())} ${two(at.getHours())}.${two(at.getMinutes())}.${two(at.getSeconds())}`;
}

/** One channel of samples (-1 to 1) as a 16-bit PCM WAV file. */
export function wav(samples: Float32Array, rate: number): Blob {
  const data = samples.length * 2;
  const view = new DataView(new ArrayBuffer(44 + data));
  const ascii = (at: number, s: string) => { for (let i = 0; i < s.length; i++) view.setUint8(at + i, s.charCodeAt(i)); };
  ascii(0, 'RIFF'); view.setUint32(4, 36 + data, true); ascii(8, 'WAVE');
  ascii(12, 'fmt '); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
  view.setUint32(24, rate, true); view.setUint32(28, rate * 2, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true);
  ascii(36, 'data'); view.setUint32(40, data, true);
  for (let i = 0; i < samples.length; i++) {
    const s = Math.max(-1, Math.min(1, samples[i]!));
    view.setInt16(44 + i * 2, Math.round(s < 0 ? s * 0x8000 : s * 0x7fff), true);
  }
  return new Blob([view.buffer], { type: 'audio/wav' });
}
