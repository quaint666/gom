# Gom: design mockups

Drawn 2026-11-24, voice calls added 2026-11-26. Thirty screens, each in light and dark: desktop at 1180 px wide, phone at 390 px wide. All text is Vietnamese; names, IDs, orders and the QR code are samples.

## The easiest way to look
Open the images in `mockups/` (lossless WebP; any browser or GitHub shows them). Dark mode is in `mockups/dark/` under the same file names.

| File | What |
|---|---|
| 01 main chat (desktop), 02 chat list, 03 thread | The main screen: list on the left, thread on the right; on a phone one pane at a time |
| 04 sign up, 05 sign in (wrong password), 06 invite expired | Invite and sign-in |
| 07 new conversation, 08 add friend, 11 add friend (desktop dialog) | Starting a conversation and adding friends |
| 09 group info, 10 add members | Group members |
| 12 empty list, 13 loading list, 20 no conversation open (desktop) | Empty and loading |
| 14 reconnecting, 15 send failed | Connection and sending |
| 16 image viewer, 17 Ops room | Images and the staff-only Ops room |
| 18 message menu, 19 recalled messages and replying | Message actions |
| 21 calling, 22 and 23 ringing on the lock screen (name hidden, shown), 24 ringing while the phone is in use, 25 in a call, 26 where the sound goes, 27 reconnecting, 28 call ended, 29 call rows in a chat, 30 a missed call in the list | Voice calls, in the Android app (screen 24 is Android's own notification, sketched) |

`design-notes.md` has the colours, type, shapes and the product rules the screens assume.

## The sources
`source/` holds four real HTML pages (`main-chat.html`, `onboarding-and-friends.html`, `states.html`, `calls.html`) with the CSS and the fonts. Open one in a browser to see every screen on one page, with a Light/Dark switch.

They are mockups, not the app: the buttons do nothing, the data is hard-coded in the page, and the layout is hand-written for these screens. The colour tokens and the component CSS are worth copying into the real frontend; the rest is not.

`source/fonts/` is Be Vietnam Pro (SIL Open Font License, licence text beside it), copied from the Peekbox repository.

## Exporting the images again
Each page can show one frame alone: add `?frame=<index>&theme=light|dark` to the URL. Frames are numbered in page order, from 0. Example, screen 15 in dark mode, on a phone:

    chromium-browser --headless=new --hide-scrollbars --force-device-scale-factor=2 \
      --window-size=390,780 --virtual-time-budget=3000 --screenshot=out.png \
      "file://$PWD/design/source/states.html?frame=3&theme=dark"

| Page | Frame 0, 1, 2 ... |
|---|---|
| `main-chat.html` | desktop (01), chat list (02), thread (03) |
| `onboarding-and-friends.html` | sign up (04), sign in (05), invite expired (06), new conversation (07), add friend (08), group info (09), add members (10), add friend dialog on desktop (11) |
| `states.html` | empty list (12), loading list (13), reconnecting (14), send failed (15), image viewer (16), Ops room (17), message menu (18), recalled and replying (19), no conversation open on desktop (20) |
| `calls.html` | calling (21), lock screen with the name hidden (22) and shown (23), phone in use (24), in a call (25), where the sound goes (26), reconnecting (27), ended (28), call rows (29), missed call in the list (30) |

Window sizes: phone 390 x 780 (screen 08 is 390 x 960, screens 02 and 03 are 390 x 760), desktop 1180 x 700. The images are made at 2x for phones and 1x for desktop, then saved as lossless WebP.

## Things that are not obvious
- **The mockups are a snapshot of 2026-11-24, before most of the app was built.** Stickers and emoji, profile pictures, notifications, account deletion and the owner's storage page are not drawn, and screen 17 (the "Ops room") shows a plan that changed (rule 7 in `design-notes.md`). The rules in `design-notes.md` and `docs/decisions.md` are what the app follows.
- **The call screens (21 to 30) are a plan, drawn 2026-11-26 before any of it is built.** They are native screens of the Android app, drawn in Gom's look; screen 24 is Android's own call notification, which One UI draws its own way, so only its words, picture and buttons are Gom's. The icons for calls were drawn for these mockups.
- **Each screen shows one moment, not behaviour.** Hover, focus and pressed looks follow the Peekbox rules in `design-notes.md` and are not drawn per screen. Animation, tablet width and the desktop message menu are not drawn.
- **No screen shows the password reset, the list of your sessions, or the display-name field yet.** Rule 11 in `design-notes.md` says they exist; they are not drawn.
- **The QR code is a fake pattern** and does not scan.
- **Contrast** of the colour pairs was calculated (see the notes); it was not tested on real screens or with a screen reader.
