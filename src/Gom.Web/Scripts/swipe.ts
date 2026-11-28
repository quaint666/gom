/** How far a message has to be dragged, in CSS pixels, before letting go replies to it. */
export const SWIPE_READY = 60;
/** Past `SWIPE_READY` the message follows the finger at this share of its speed, so it feels held back. */
const BEYOND = 0.3;
/** The message never goes further than this. */
const SWIPE_MAX = 100;

/**
 * How far a message moves for a finger that has moved `dx` sideways since it touched down: a message from someone else goes right,
 * your own goes left (away from the screen edge it sits against), and a drag the other way moves nothing. Always zero or more.
 */
export function swiped(dx: number, mine: boolean): number {
  const pull = Math.max(0, mine ? -dx : dx);
  return pull <= SWIPE_READY ? pull : Math.min(SWIPE_READY + (pull - SWIPE_READY) * BEYOND, SWIPE_MAX);
}

/** Whether letting go now replies. */
export const swipeReady = (moved: number): boolean => moved >= SWIPE_READY;
