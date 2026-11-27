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
2. When what is on `main` should go to the family, a pull request named "Release 1.1.0" changes `VERSION` and adds the
   release's entry under "What's new" below.
3. That commit is deployed to staging, checked, and promoted to production.
4. The commit production runs is tagged `v1.1.0`, and the release is published on GitHub with its generated notes. A tag
   is public and permanent, so it is made only when the owner says so.

Between releases, staging may run newer code than its number says: the number moves only with step 2.

## What's new

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
