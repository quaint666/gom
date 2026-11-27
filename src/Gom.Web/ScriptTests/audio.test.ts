import { render } from 'lit-html';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { audioCard, clockOf } from '../Scripts/chat/audio.js';
import { flush } from './helpers.js';

const song = { id: 'a1', kind: 'file' as const, name: 'Bai-hat-ru-con.mp3', size: 4_300_000, width: null, height: null, audio: true };
let host: HTMLElement;
/** jsdom plays nothing: play and pause are counted, and the time and length are set by hand. */
const media = { play: vi.fn(), pause: vi.fn() };
beforeEach(() => {
  host = document.createElement('div'); document.body.replaceChildren(host);
  media.play.mockReset(); media.pause.mockReset();
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockImplementation(function (this: HTMLMediaElement) { media.play(this); return Promise.resolve(); });
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(function (this: HTMLMediaElement) { media.pause(this); });
});
afterEach(() => { vi.restoreAllMocks(); });

const set = (el: HTMLMediaElement, props: Partial<Record<'paused' | 'duration' | 'currentTime', number | boolean>>) => {
  for (const [k, v] of Object.entries(props)) Object.defineProperty(el, k, { value: v, configurable: true, writable: true });
};

describe('a sound file in a message', () => {
  it('shows its name and size, and fetches nothing until played, then plays it from ?play=true', async () => {
    render(audioCard(song), host);
    const sound = host.querySelector('audio')!;
    expect(host.querySelector('b')!.textContent).toBe('Bai-hat-ru-con.mp3');
    expect(host.querySelector('[data-audio-time]')!.textContent).toBe('4,1 MB');
    expect(sound.getAttribute('preload')).toBe('none');
    expect(sound.getAttribute('src')).toBe('/api/attachments/a1?play=true');
    expect(host.querySelector('a[download]')!.getAttribute('href')).toBe('/api/attachments/a1');   // still saved as a file
    set(sound, { paused: true });
    host.querySelector<HTMLButtonElement>('[data-audio-play]')!.click();
    await flush();
    expect(media.play).toHaveBeenCalledWith(sound);
  });

  it('stops any other sound first, so two never play over each other, and pauses on a second press', async () => {
    render([audioCard(song), audioCard({ ...song, id: 'a2' })] as never, host);
    const [first, second] = [...host.querySelectorAll('audio')];
    set(first!, { paused: false }); set(second!, { paused: true });
    host.querySelectorAll<HTMLButtonElement>('[data-audio-play]')[1]!.click();
    await flush();
    expect(media.pause).toHaveBeenCalledWith(first);
    expect(media.play).toHaveBeenCalledWith(second);
    set(second!, { paused: false });
    host.querySelectorAll<HTMLButtonElement>('[data-audio-play]')[1]!.click();
    expect(media.pause).toHaveBeenCalledWith(second);
  });

  it('shows how far it has played, moves when the bar is dragged, and shows a pause button while it plays', () => {
    render(audioCard(song), host);
    const sound = host.querySelector('audio')!;
    set(sound, { duration: 204, currentTime: 72 });
    sound.dispatchEvent(new Event('timeupdate'));
    expect(host.querySelector('[data-audio-time]')!.textContent).toBe('1:12 / 3:24');
    const seek = host.querySelector<HTMLInputElement>('[data-audio-seek]')!;
    expect(seek.value).toBe('353');
    seek.value = '500'; seek.dispatchEvent(new Event('input'));
    expect(sound.currentTime).toBe(102);
    const card = host.querySelector('[data-audio]')!;
    sound.dispatchEvent(new Event('play'));
    expect(card.hasAttribute('data-playing')).toBe(true);
    sound.dispatchEvent(new Event('ended'));
    expect(card.hasAttribute('data-playing')).toBe(false);
  });

  it('writes times as a player does', () => {
    expect([clockOf(0), clockOf(7.9), clockOf(204), clockOf(3723), clockOf(Number.NaN)]).toEqual(['0:00', '0:07', '3:24', '1:02:03', '0:00']);
  });
});
