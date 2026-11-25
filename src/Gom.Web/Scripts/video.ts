// Makes a video picked on this device ready to send, on this device: an MP4 (H.264 pictures, AAC sound) no larger than 720p and
// within the server's limit (videoPlan.ts), which every browser plays, and its first picture to show before it plays. Built as its
// own file (video.js) and loaded only when someone picks a video, so pages do not carry it. The work is done by the device's own
// video encoder (WebCodecs), through Mediabunny.
import { BlobSource, BufferTarget, CanvasSink, Conversion, Input, MATROSKA, MP4, Mp4OutputFormat, Output, QTFF, WEBM, type InputVideoTrack } from 'mediabunny';
import type { PreparedVideo } from './pages/chat.js';
import { videoPlan } from './videoPlan.js';

/** The still picture: its longer side (the server makes it smaller again). */
const POSTER_EDGE = 640;

/**
 * The video made ready within maxBytes, or null when this device cannot do it (no video encoder, a format it cannot read, or a track
 * it would have to drop) or the video is too long to fit: the caller then sends the original if it is small enough. The pictures
 * are always encoded again, to the bitrate the plan chose.
 */
export async function prepareVideo(file: File, onProgress: (fraction: number) => void, maxBytes: number): Promise<PreparedVideo | null> {
  // What phones and cameras record: MP4, QuickTime (an iPhone's .mov), WebM and Matroska. Other formats go as they are.
  const input = new Input({ source: new BlobSource(file), formats: [MP4, QTFF, WEBM, MATROSKA] });
  try {
    const track = await input.getPrimaryVideoTrack();
    if (track === null) return null;
    const sound = await input.getPrimaryAudioTrack();
    const plan = videoPlan(track.displayWidth, track.displayHeight, await input.computeDuration(), maxBytes, sound !== null);
    if (plan === null) return null;
    const output = new Output({ format: new Mp4OutputFormat({ fastStart: 'in-memory' }), target: new BufferTarget() });
    const conversion = await Conversion.init({
      input, output, showWarnings: false,
      video: { width: plan.width, height: plan.height, fit: 'contain', codec: 'avc', bitrate: plan.bitrate },
      // No quality or bitrate asked of the sound: AAC sound (what phones record) is then copied as it is, which needs no AAC encoder
      // (Chrome on Linux has none); other sound is encoded to AAC where the device can.
      audio: { codec: 'aac' },
    });
    // A track left out (sound this device cannot encode, say) would make a different video from the one chosen.
    if (!conversion.isValid || conversion.discardedTracks.length > 0) return null;
    conversion.onProgress = (fraction) => onProgress(fraction);
    await conversion.execute();
    const bytes = output.target.buffer;
    if (bytes === null) return null;
    return { video: new Blob([bytes], { type: 'video/mp4' }), poster: await firstPicture(track) };
  } catch {
    return null;
  } finally {
    input.dispose();
  }
}

/** The video's first picture as a JPEG, or null when it cannot be had (the video still goes). */
async function firstPicture(track: InputVideoTrack): Promise<Blob | null> {
  try {
    const scale = Math.min(1, POSTER_EDGE / Math.max(track.displayWidth, track.displayHeight));
    const sink = new CanvasSink(track, { width: Math.round(track.displayWidth * scale), height: Math.round(track.displayHeight * scale), fit: 'contain' });
    const frame = await sink.getCanvas(await track.getFirstTimestamp());
    if (frame === null) return null;
    const canvas = frame.canvas;
    return canvas instanceof HTMLCanvasElement
      ? await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.8))
      : await canvas.convertToBlob({ type: 'image/jpeg', quality: 0.8 });
  } catch {
    return null;
  }
}
