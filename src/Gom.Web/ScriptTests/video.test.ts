import { describe, expect, it } from 'vitest';
import { fitted } from '../Scripts/video.js';
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
