# Releases

Gom is released as a whole: the server, the pages and the Android app share one number. The list of every release, with
each pull request in it, is on GitHub's Releases page; this file says how a release is made and what each one brought.

## The number

Semantic versions, `MAJOR.MINOR.PATCH`, kept in the `VERSION` file at the root of the repository. It is the only place the
number is written: the server reads it through the build and shows it in Settings ("Phiên bản v1.0.0"), and the Android
app takes it as its version name.

- **Minor** (1.1.0): anything new for the people using Gom.
- **Patch** (1.0.1): fixes only.
- **Major** (2.0.0): something people or operations must act on, such as an app and a server that can no longer work
  together.
- Changes to the build, CI or docs alone do not need a release; they go out with the next one.

## Making a release

1. Each pull request carries one type label: `enhancement`, `bug`, `ci` or `documentation`. GitHub sorts a release's
   notes by it (`.github/release.yml`).
2. When what is on `main` should go to the family, a pull request named "Release 1.1.0" changes `VERSION`, adds the
   release's entry under "What's new" below, and adds the same release, first, to the app's "Có gì mới" page
   (`whatsNew` in `Scripts/strings/vi.ts` and `en.ts`, written for the people who use Gom, with no dates): Settings
   names the first entry as the latest, and the page keeps every release from 1.0.0.
3. That commit is deployed to staging, checked, and promoted to production.
4. The commit production runs is tagged `v1.1.0`, and the release is published on GitHub with its generated notes. A tag
   is public and permanent, so it is made only when the owner says so. The notes start with the versions line (below).

## Which versions are current

The web (the server and its pages) is always the newest release: production runs it, and every open page moves to it. The
Android app is a frame around those pages plus what only an app can do (notifications, calls, the share sheet), so it is
built and sent to Google Play less often and may be a release or two behind. That is expected: most of what a release
brings reaches the app through the pages at once. Each release's notes on GitHub start with one line saying both, so
nobody has to guess:

    **Versions:** web 1.3.0 (live now) · Android app 1.1.0 (Google Play)

The app's version number is the release it was built from. Settings shows both: "Phiên bản v1.3.0 · ứng dụng 1.1.0".
APKs are not attached to releases: the app is installed from Google Play.

Between releases, staging may run newer code than its number says: the number moves only with step 2.

## What's new

### 1.5.0 (29 November 2026)

Two looks changed after 1.4.0 (#233). The web side is live on deploy; the Android app needs this build for the call
screen, so `gom-1.5.0.apk` is built.

**Changes**
- A reaction is a small round badge on the corner of its bubble, as Messenger draws it: the left corner on your own
  messages, the right corner on other people's (#233).
- The call screens, on the web and in the app, no longer show the small "Gom" label and logo at the top (#233).

### 1.4.0 (29 November 2026)

Everything from #216 to #231: how a message and its reactions look and answer on a phone, who is typing, mentions, the
owner's announcement channel, and a page in the app that says what changed. The Android app needs nothing new to work
(the pages and the notification words come from the server); `gom-1.4.0.apk` is built so the app's number matches.

**New**
- A reaction sits at its bubble's edge: the left edge on your own messages, the right edge on other people's (#218,
  #226).
- On a phone only: swipe a message to reply (#219) and double tap a message to heart it (#218).
- "Bảo, Mai, Lan và 2 người khác đang nhập…": a line above the message field says who is typing, at most three names;
  it follows the read-receipts switch (#220).
- Mentions: write `@name` or `@all` in a group; the names are lit in the words, the chat list marks a mention, an
  arrow takes you to the first one, and a named member is told even through a mute (never for `@all`, and not if they
  blocked the writer). One migration, `Mentions` (#221, #222).
- The announcement channel, "Thông báo": first in everyone's list, everyone is in it, only the owner writes, everyone
  reads, reacts and may mute it; no replies, only the owner pins, and it cannot be hidden or deleted. One migration,
  `Announcements` (#227, #228).
- "Có gì mới": a page in the app with what changed in each release from 1.0.0 (newest open, older ones folded), and a
  row in Settings under "Tải lại Gom" that names the latest (#229).
- Copying a message says so in a small toast above the message field, and in a group the picture beside someone's
  last message opens their card (#225).

**Changes**
- The stylesheet is built from files in `src/Gom.Web/Styles/` by `npm run build`; `gom.css` is no longer in git. People
  see no difference (#216, #217).
- In Settings, "Mã nguồn và báo lỗi" moved down beside the setup guide and the policy (#229).

### 1.3.0 (28 November 2026)

Everything from #199 to #211: what Google Play's testers need, Gom's new mark, a setup guide, and Gom kept fast and
steady with a long history (measured on a copy with 10 million messages).

**New**
- The owner's page is five parts of Settings (invites, requests to join, members, sticker packs, contact), each with its
  own page and a count on its row; refusing a request asks first; past links fold away (#200).
- An invite link can add whoever joins to one of the owner's groups, and approving a request can too (#201).
- A new account sees a welcome page once: what Gom is, and the way in (#202).
- Groups have a picture of their own, set by the leader or a deputy (#203).
- A sticker pack shows for everyone, or only for the people and in the chats the owner chooses; the server keeps to it
  (#204).
- Gom's new mark: a speech bubble with three dots, on the web and in the Android app (#205).
- Where the code is and where to report a bug, a private way to report a security hole, and the app's version beside the
  site's in Settings (#206).
- A setup guide (`/help`): notifications, the microphone, and on Android the battery, for each kind of device; linked
  from sign-in, joining, the welcome page and Settings (#209).

**Fixes**
- Background names fit their tiles; a 1:1 chat's header shows only the Gom ID; the question before disbanding stands
  where its button was (#199).
- Going to an older message (a quote, a pin, a search result) or an older picture loads in one go: 2,000 messages back
  took 38 s on a slow phone, now under 5 (#208).
- Searching a long conversation for a word it does not have took seconds; now a few hundredths. Unread counts past 99
  show "99+" (#210).
- Trimming old messages no longer holds up sending in those chats, and a group with a long history can be disbanded
  (#211).

### 1.2.1 (28 November 2026)

#196 and #197: fixes only.

**Fixes**
- A message with two to five pictures now shows in Firefox and Safari, and so on iPhones; there it was an empty dot
  (#197).
- A conversation opens at its newest message even when a picture above it loads late: pictures, moving pictures and
  videos keep their place while they load. On a computer a video is as wide as pictures (#197).
- A quote of a message with only a picture, video or file says what it was ("Hình ảnh", "Video", "Tệp"), where it was
  empty, and a tap on any quote goes to the message it answers (#196).

### 1.2.0 (28 November 2026)

Everything from #172 to #193: H's fourteen changes to how Gom looks and works, and the Android app's release build.

**New**
- Voice messages: while there is nothing to send, the microphone takes the send button's place; a tap records up to five
  minutes, with a bin to throw it away.
- Sound files (MP3, AAC, OGG, WAV, FLAC, sound-only MP4) play in the message, with a seek bar; one at a time.
- Two-step sign-in, each person's choice: a 6-digit code from an authenticator app after the password. The owner's reset
  link turns it off; the owner's own lost phone has a server command.
- Disappearing messages: 15 minutes to a week, per conversation; in a group set by the leader and the deputies.
- Sticker packs the owner makes from their own pictures, in everyone's picker after Gom's own.
- Eight gradient colours, your own messages in them as in Messenger.
- The search above the list finds friends (also those never written to) and groups; Contacts shows the five most
  recent, then everyone A–Z. The search through every conversation's messages is gone.
- Each row's ⋯ (members and invites on the owner's page, friends in Contacts) opens the chat list's menu.
- Smoother movement: pages cross-fade, a phone's conversation slides out on back, menus and the info panel close
  animated, tabs fade.
- On a computer, a strip of the conversation's pictures under the picture viewer.
- In the Android app: the ringing screen's buttons higher and closer, with a pulse on Answer, "Cuộc gọi thoại đến",
  and the caller's picture; the page may use the microphone (voice messages).
- Someone's card closes with a tap outside it.
- The Android app's release build for Google Play (not on Play yet).

**Fixes**
- Gom open in a phone's browser no longer rings beside the app for the same call: each browser chooses whether it takes
  calls (Settings › Thông báo), off by default on a phone whose person has the app (#180).
- The privacy page's way back is a button ("Về trang chính"); the accent bar runs straight across the top on a
  computer; the ⓘ button is filled while its panel is open (#177).
- The group page's name box in the site's own style (#188).

### 1.1.0 (27 November 2026)

Everything from #152 to #171: voice calls on the web and in the Android app.

**New**
- Voice calls between friends, one to one, from a chat's header or "Gọi lại" on a missed call. Every device of the person
  called rings for 45 seconds; each call leaves a line in the chat (its length, or missed, declined or busy).
- Calls in the browser, on computers and phones, with a bar that keeps the call going while you read the chat.
- Calls in the Android app: a ringing screen over the lock screen, the speaker, a headset or Bluetooth through Android's
  call system, and a check that the phone lets Gom ring (sleeping apps, full-screen ringing).
- A call goes on through a change of network, and one that cannot connect ends after 30 seconds and says so.
- GIFs from the keyboard in the Android app.
- The release number in Settings.

### 1.0.0 (26 November 2026)

The first release: everything from the first pull request to #150, as production ran it that day.

**New**
- Accounts: joining by invite, or by asking and being approved by the owner; friends and blocking; profile pictures;
  Vietnamese, or English as each person's choice; deleting your own account.
- Chat: one-to-one and group conversations with live delivery and offline sending; replies, forwarding, pins, reactions,
  editing, taking a message back, delete for me, read receipts.
- Groups: roles, removal, approval to join, bans.
- Media: pictures and files; videos made smaller on the sending device (up to 45 MB); moving pictures (GIFs); emoji and 432
  stickers in eleven packs.
- Search as you type, in one conversation or all of them.
- Each conversation's menu: pin to the top, mark unread, hide, delete for yourself; mute for a while or until turned back on.
- Sixteen colours, fifteen backgrounds and pure black, per person or per conversation.
- Notifications in browsers (Web Push), at three levels: the words, only the name, or nothing.
- Settings as a menu with an address per part; open pages move to a new version by themselves, keeping what was typed.
- For the owner: a storage page that trims old pictures, files and messages; requests to join; contact messages from the
  privacy page.
- The groundwork for voice calls (not usable yet): the server's call steps, call lines in the chat, relay logins and phone
  wake-ups.
- The Android app (not on Google Play yet): Gom in a web view, with notifications through Firebase Cloud Messaging.

**Fixes**
- The image build's start-up crash: the database connection no longer tries Kerberos encryption (#148).
