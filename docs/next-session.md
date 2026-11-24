# Next session: research and plan

Written 2026-11-24, at the end of the session that built PRs #46 to #59. Nothing here is built yet. Claims are marked
**[verified]** (seen in the code or real output), **[stated]** (a source says so; link given), or **[inferred]** (reasoned, not tried).
Search results were read as summaries, not as the full pages, so the sources are leads to confirm before building.

## 1. Where things stand

- Merged: the 12-item stack (#46 to #56), the install button (#57), the attach button hidden when uploads are off (#58), and the screenshot fixes (#59).
- Uploads are on in production and in staging. Link previews are off in both until the container-to-host network check is done (operations side).
- Not tried on a real phone: PWA install through the new button, the keyboard and blank-strip fix (the page itself cannot scroll now), the sheet animations.
- Known limits: the invite link is shown once; forwarding copies words only; the side panel lists only loaded media; no update prompt for a new service worker.

## 2. Work queue (agreed order)

Small items first, each its own PR:

1. Side-panel photos and files from a server list (not just what is loaded).
2. An update prompt when a new version is deployed.
3. Forwarding carries pictures and files.
4. A per-socket limit on `sync` requests.
5. Fingerprinted static file names (safer caching than today's network-first rule).
6. README updates: uploads, previews off by default, the proxy body-size limit, the backup note.

Then the larger items below. Optional, later: a load test at the 256 MB limit with several people uploading; a second full review of the repo.

## 3. Web Push (item 12)

### Agreed decisions (H, 2026-11-24)

1. A notification shows the sender's name and the conversation, never the message text.
2. Only devices with no open Gom page are pushed (a connected device already has the live message).
3. Switched on per device from Settings ("Bật thông báo"), with an off switch. No permission pop-up on first visit.
4. The signing key (VAPID) is a configuration secret; operations confirms the servers can reach the push services outbound on port 443.
5. A per-conversation mute (section 4), built together with push.

### What the research found

- **iPhone:** web push works only for a web app added to the Home Screen (iOS and iPadOS 16.4 or later), with a manifest whose display is standalone. The permission request must run directly in the handler of a tap. A refusal is not asked again. [stated: WebKit, <https://webkit.org/blog/13878/web-push-for-web-apps-on-ios-and-ipados/>] So on iPhone the Settings button must first explain "add to Home Screen", and only then ask.
- **Every push must show a notification.** Safari revokes the permission of a site whose service worker receives a push and shows nothing. [stated: same source] So the service worker must always call `showNotification`, even when the payload cannot be read.
- **What the push services see.** The payload is encrypted for the browser, but the push service (Google, Apple, Mozilla) still sees that a push happened, when, and for which device; governments have asked for this metadata. [stated: EFF, <https://www.eff.org/deeplinks/2026/04/how-push-notifications-can-betray-your-privacy-and-what-do-about-it>; study of 21 messaging apps, <https://petsymposium.org/popets/2024/popets-2024-0151.php>] This is why decision 1 sends no message text, and why nothing identifying (user id, phone) goes in the payload beyond the sender's display name.
- **How Signal does it.** A setting with three levels: name and content, name only, nothing (just "new message"). [stated: <https://support.signal.org/hc/en-us/articles/360043273491-Notification-Options>] Gom's agreed default is Signal's "name only". Not decided: whether to add the "nothing" level as a per-device choice. Lean: yes, it is one more option on the same switch.
- **.NET libraries** (all unverified for upkeep): ClosureOSS.WebPush (2.5.7 on NuGet, aes128gcm and VAPID), Rask.WebPush (0.23.0, no dependencies), OllyWP.Core (1.0.1, needs .NET 10). [stated: NuGet pages; <https://github.com/closureOSS/webpush-csharp>] Before choosing: check release dates and open issues on the repository, and read the code that touches the key. The alternative is writing the encryption ourselves (RFC 8291 with `System.Security.Cryptography`): small but easy to get subtly wrong. Lean: a library, pinned, with a test that decrypts what it produced.

### Design to write down and agree before code

- A `push_subscriptions` table: user, endpoint, the two keys, user agent label, created and last-used times; unique on endpoint.
- Subscribing and unsubscribing are REST calls with the Origin check. A push that comes back 404 or 410 deletes the subscription.
- Who is notified: members of the conversation other than the sender, not muted, not hidden, and (decision 2) without a live socket. Joined-sequence rules apply as everywhere (nobody is told about what they may not read).
- Sending happens after the commit, off the request path, through a bounded queue and a background worker, like link previews. A slow or failing push service never delays a send. [inferred, follows the existing pattern]
- Notices (added, removed, renamed) do not notify. Edits and reactions: not in the first version.
- Tapping the notification opens the conversation; if a Gom window is already open, focus it.
- Tests: encryption round trip, a subscription of someone removed from the group gets nothing, a muted conversation gets nothing, a dead endpoint is deleted, a failing push service does not fail the send. Each guard broken on purpose.

## 4. Mute per conversation

What other apps do:

- Signal: mute for 1 hour, 8 hours, 1 day, 1 week or always. [stated: <https://support.signal.org/hc/en-us/articles/360043273491-Notification-Options>]
- WhatsApp: 8 hours, 1 week, always (sources disagree on a one-year option). [stated: <https://www.phonearena.com/news/WhatsApp-mute-chat-notifications-forever-update_id128006>]
- Telegram: custom durations, and a "disable sound" mode that still shows the notification. [stated: <https://www.androidcentral.com/apps-software/telegram-update-gives-you-more-control-of-notification-tones-and-mute-durations>]
- Whether mentions break through a mute: not confirmed by any source found. Gom has no mentions yet, so this does not arise.

Lean for Gom:

- Durations 1 hour, 8 hours, 1 day, 1 week, always (Signal's set).
- Stored on the server as `muted_until` per (user, conversation), so every device of the person obeys it, and push respects it without the client's help.
- A muted conversation still counts unread messages and still updates live; it only sends no push. A small muted icon in the list.
- Set from the conversation's menu and from the info panel; unmuting is one tap.

## 5. GIFs and stickers

### Today

- A GIF is not recognised as a picture: only JPEG, PNG and WebP are decoded (`ImageProcessor`), so a GIF is stored as a plain file and shows as a download card. [verified in the code]
- An animated WebP is recognised as WebP and re-encoded; the decoder most likely keeps only the first frame. [inferred, not tried]

### What others do

- Tenor's API for third parties ended on 30 June 2026; apps moved to Giphy or Klipy. [stated: <https://9to5google.com/2026/06/30/google-tenor-api-gif-updates/>] Giphy now charges for production keys. Klipy is free for small projects and ad-supported; its terms were not read. [stated: <https://zuplo.com/learning-center/klipy-api>] Every built-in GIF search sends what a person types to a third party, and needs CSP changes (`img-src`, `connect-src`).
- Large apps usually turn GIFs into short silent looping videos because they are much smaller. [inferred from general knowledge; the search did not confirm it] That needs a video encoder in the image, which is heavy for a 256 MB container.
- Signal stickers: a pack has a random id and a key; names and images are encrypted with the key; a pack is shared as a link carrying the id and key; images are WebP, 512 x 512 (cover PNG or WebP), up to 200 per pack. [stated: <https://signal.org/blog/make-privacy-stick>, <https://support.signal.org/hc/en-us/articles/360031836512-Stickers>] Importing one means downloading and decrypting from Signal's servers with an unofficial client; the terms of use were not checked.

### Lean

1. **Animated pictures, step one:** accept GIF and animated WebP as a picture kind that is stored as it is (size capped, dimensions checked from the header, no metadata promise), and shown with `<img>` so it moves. No video conversion, no search. The phone keyboard's own GIF button already inserts images, so this covers most use. Open question: stripping metadata from a GIF without re-encoding.
2. **Stickers, step two:** our own packs. An owner (later, any member) uploads a set of WebP or PNG images up to 512 px; a sticker tray in the composer; a sticker is sent as a message with a `sticker` attachment, shown without a bubble. The pack list and images are ordinary rows and files, so backup and deletion rules apply unchanged.
3. **Not now:** built-in GIF search (third party, cost, CSP) and Signal import (unofficial, terms unchecked). Revisit if the family asks.

## 6. Owner: delete an account and its data

Agreed leans (H, 2026-11-24):

1. Two separate actions: disable (exists, reversible) and a new permanent "Xóa tài khoản".
2. Their messages in chats with other people stay, shown as "Tài khoản đã xóa", so other people's conversations stay readable.
3. A checkbox to also delete the owner's own 1:1 conversation with that account entirely. ("All the chat I had with that account" is read as this conversation only, not everything the account wrote anywhere.)
4. Always removed: profile, password, sessions, friendships, group memberships (each with a "đã xóa" notice), read marks, reactions, and their uploads that no message needs.
5. The Gom ID is reserved forever, so nobody can impersonate a deleted person.
6. Confirmation by typing the Gom ID, with an "this cannot be undone" text, and a recorded line (who, when, no content).
7. Built with one fixed "deleted account" placeholder user that their messages move to, so the schema keeps its rules. All links to users are "restrict" today, so a user row cannot be deleted while anything points at it. [verified earlier from the model]
8. Backups: a backup that only adds files keeps deleted pictures forever. Ask operations whether deleted files can be excluded or expired; until then, say so in the dialog. Also decide how long database dumps are kept, since deleted data lives in them.

Later: any member can delete their own account the same way.

Rehearse on a copy of a real dump before shipping, as with the earlier migrations.

## 7. Not verified yet

- Anything on a real phone (see section 1).
- Link previews on the real internet, IPv6-only sites, and a name answering with both public and private addresses.
- That the app can open a file restored from backup (no sign-in-free read path exists; the restore test proves only that the database and files agree).
- Every claim in sections 3 to 5 marked **[stated]** or **[inferred]**.
