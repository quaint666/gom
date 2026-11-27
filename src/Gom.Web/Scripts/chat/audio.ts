import { html, type TemplateResult } from 'lit-html';
import type { Attachment } from '../api.js';
import * as icons from '../icons.js';
import { formatSize } from '../text.js';
import { say } from '../say.js';

/**
 * A sound file in a message (an attachment the server found to be sound: `audio`): a play button, how far it has played, and a way to
 * save it. The browser's own <audio> plays it, fetched only when played (`?play=true`, a piece at a time); this card is its controls,
 * in Gom's look. Its moving parts change the card itself, not the whole page: a page drawn again keeps playing.
 */

/** A time as a player shows it: "0:07", "3:24", "1:02:03". */
export function clockOf(seconds: number): string {
  const all = Number.isFinite(seconds) && seconds > 0 ? Math.floor(seconds) : 0;
  const h = Math.floor(all / 3600), m = Math.floor((all % 3600) / 60), s = all % 60;
  const two = (n: number) => String(n).padStart(2, '0');
  return h > 0 ? `${h}:${two(m)}:${two(s)}` : `${m}:${two(s)}`;
}

const parts = (e: Event) => {
  const card = (e.currentTarget as Element).closest<HTMLElement>('[data-audio]')!;
  return { card, sound: card.querySelector('audio')!, seek: card.querySelector<HTMLInputElement>('[data-audio-seek]')!, time: card.querySelector('[data-audio-time]')! };
};

/** Where the card says it is: the bar and "played / length". */
function show(e: Event): void {
  const { sound, seek, time } = parts(e);
  const length = Number.isFinite(sound.duration) ? sound.duration : 0;
  seek.value = String(length > 0 ? Math.round((sound.currentTime / length) * 1000) : 0);
  time.textContent = `${clockOf(sound.currentTime)} / ${clockOf(length)}`;
}

/** Play or pause; any other sound in the page stops first, so two never play over each other. */
function toggle(e: Event): void {
  const { sound } = parts(e);
  if (!sound.paused) { sound.pause(); return; }
  for (const other of document.querySelectorAll('audio')) if (other !== sound && !other.paused) other.pause();
  void Promise.resolve().then(() => sound.play()).catch(() => undefined);   // one that cannot play it leaves the card as it was; saving still works
}

function seekTo(e: Event): void {
  const { sound, seek } = parts(e);
  if (Number.isFinite(sound.duration) && sound.duration > 0) sound.currentTime = (Number(seek.value) / 1000) * sound.duration;
}

const playing = (on: boolean) => (e: Event) => { parts(e).card.toggleAttribute('data-playing', on); };

export const audioCard = (a: Attachment): TemplateResult => html`
  <div class="audiocard" data-audio=${a.id}>
    <button class="pb" type="button" data-audio-play aria-label=${say.chat.playSound(a.name)} @click=${toggle}>
      <span class="when-paused">${icons.play()}</span><span class="when-playing">${icons.pause()}</span></button>
    <div class="mid">
      <b>${a.name}</b>
      <input class="seek" type="range" min="0" max="1000" value="0" data-audio-seek aria-label=${say.chat.soundPosition} @input=${seekTo}>
      <small data-audio-time>${formatSize(a.size)}</small>
    </div>
    <a class="ib small" href=${`/api/attachments/${a.id}`} download=${a.name} aria-label=${say.chat.save} data-file=${a.id}>${icons.download()}</a>
    <audio preload="none" src=${`/api/attachments/${a.id}?play=true`}
      @loadedmetadata=${show} @timeupdate=${show} @play=${playing(true)} @pause=${playing(false)} @ended=${playing(false)}></audio>
  </div>`;
