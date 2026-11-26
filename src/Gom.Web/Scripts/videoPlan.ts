// How a picked video is sent so that it fits the server's limit: its size and its bitrate, chosen from its length before it is
// encoded. Kept apart from the converter (video.ts) so the page can say how long a video may be without loading it.

/** 720p: the shorter side at most 720 pixels. */
const HD = 720;
/** 480p, for a video too long to fit at 720p. */
const SD = 480;
/** The bitrate a short video gets, per pixel of a picture (2 Mbit/s at 1280×720). */
const PEAK_BITS_PER_PIXEL = 2_000_000 / (1280 * 720);
/** Below this a 720p picture turns to mush, so the video goes at 480p instead. */
const HD_FLOOR = 800_000;
/** Below this even 480p does: the video is too long to send. */
const SD_FLOOR = 400_000;
/** What the sound may take: phones record AAC at up to 256 kbit/s, and it is copied as it is. */
const SOUND = 256_000;
/** The share of the limit aimed at: an encoder's bitrate is an average, and the file has its outline too. */
const FILL = 0.9;

/** The size to send: the video's own, made smaller to fit (by default) 720p either way round, with even sides (the encoder needs them). */
export function fitted(width: number, height: number, shortEdge = HD): { width: number; height: number } {
  const longEdge = Math.round(shortEdge * 16 / 9);
  const scale = Math.min(1, longEdge / Math.max(width, height), shortEdge / Math.min(width, height));
  const even = (n: number) => Math.max(2, Math.round(n * scale / 2) * 2);
  return { width: even(width), height: even(height) };
}

export interface VideoPlan { width: number; height: number; bitrate: number }

/**
 * How to send a video of this size and length (seconds) within maxBytes: 720p at up to 2 Mbit/s, less for a longer one, then 480p,
 * and null when it would not fit even at 480p's lowest bitrate.
 */
export function videoPlan(width: number, height: number, seconds: number, maxBytes: number, hasSound: boolean): VideoPlan | null {
  const budget = Math.floor(maxBytes * 8 * FILL / Math.max(seconds, 1)) - (hasSound ? SOUND : 0);
  const at = (shortEdge: number): VideoPlan => {
    const size = fitted(width, height, shortEdge);
    return { ...size, bitrate: Math.min(budget, Math.round(PEAK_BITS_PER_PIXEL * size.width * size.height)) };
  };
  if (budget >= HD_FLOOR) return at(HD);
  if (budget >= SD_FLOOR) return at(SD);
  return null;
}

/** The longest video, in seconds, that fits maxBytes with its sound: the length the page tells people. */
export function longestVideo(maxBytes: number): number {
  return Math.floor(maxBytes * 8 * FILL / (SD_FLOOR + SOUND));
}
