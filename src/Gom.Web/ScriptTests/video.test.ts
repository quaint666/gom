import { describe, expect, it } from 'vitest';
import { fitted, longestVideo, videoPlan } from '../Scripts/videoPlan.js';
import { isVideoFile, playLength } from '../Scripts/pages/chat.js';

describe('the size a video is sent at', () => {
  it('fits 720p either way round and keeps a smaller one as it is', () => {
    expect(fitted(1920, 1080)).toEqual({ width: 1280, height: 720 });
    expect(fitted(1080, 1920)).toEqual({ width: 720, height: 1280 });
    expect(fitted(3840, 2160)).toEqual({ width: 1280, height: 720 });
    expect(fitted(640, 360)).toEqual({ width: 640, height: 360 });
  });

  it('lets the shorter side decide for a squarer video, and keeps every side even', () => {
    expect(fitted(4000, 3000)).toEqual({ width: 960, height: 720 });
    expect(fitted(1440, 1440)).toEqual({ width: 720, height: 720 });
    expect(fitted(853, 479)).toEqual({ width: 854, height: 480 });
  });

  it('fits 480p when asked', () => {
    expect(fitted(1920, 1080, 480)).toEqual({ width: 854, height: 480 });
    expect(fitted(1080, 1920, 480)).toEqual({ width: 480, height: 854 });
  });
});

describe('fitting a video into the limit', () => {
  const limit = 45 * 1024 * 1024;

  it('sends a short video at 720p and 2 Mbit/s, and a smaller one at a bitrate for its size', () => {
    expect(videoPlan(1920, 1080, 60, limit, true)).toEqual({ width: 1280, height: 720, bitrate: 2_000_000 });
    expect(videoPlan(640, 360, 60, limit, true)).toEqual({ width: 640, height: 360, bitrate: 500_000 });
  });

  it('lowers the bitrate for a longer one, then goes to 480p, and gives up when even that would not fit', () => {
    expect(videoPlan(1920, 1080, 240, limit, true)).toEqual({ width: 1280, height: 720, bitrate: 1_159_577 });
    expect(videoPlan(1080, 1920, 400, limit, true)).toEqual({ width: 480, height: 854, bitrate: 593_346 });
    expect(videoPlan(1920, 1080, 600, limit, true)).toBeNull();
  });

  it('leaves the sound\'s share to the pictures when there is no sound', () => {
    expect(videoPlan(1920, 1080, 600, limit, false)).toEqual({ width: 854, height: 480, bitrate: 566_231 });
  });

  it('says how long a video with sound may be', () => {
    expect(longestVideo(limit)).toBe(517);
    expect(videoPlan(1920, 1080, 517, limit, true)).not.toBeNull();
    expect(videoPlan(1920, 1080, 519, limit, true)).toBeNull();
  });
});

describe('a video in words', () => {
  it('says how long it plays as a player does', () => {
    expect(playLength(42_000)).toBe('0:42');
    expect(playLength(61_500)).toBe('1:02');
    expect(playLength(3_723_000)).toBe('1:02:03');
    expect(playLength(0)).toBe('');
    expect(playLength(null)).toBe('');
  });

  it('knows a video by its type, or by its name when the device gives no type', () => {
    expect(isVideoFile(new File([], 'a', { type: 'video/mp4' }))).toBe(true);
    expect(isVideoFile(new File([], 'IMG_0001.MOV'))).toBe(true);
    expect(isVideoFile(new File([], 'a.jpg', { type: 'image/jpeg' }))).toBe(false);
    expect(isVideoFile(new File([], 'notes.txt'))).toBe(false);
  });
});
