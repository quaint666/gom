# Next session: where things stand and what comes next

Written 2026-11-25, brought up to date on 2026-11-26 (after #133), 2026-11-27 (after #171) and 2026-11-28 (after #193,
and again for release 1.3.0).
Claims are marked **[verified]** (seen in the code or real output), **[stated]** (someone said so; who is given), or
**[inferred]** (reasoned, not tried).

## 1. Where things stand

- Release 1.3.0 is #199 to #211, all on `main` (#201 to #211 through #214: stacked pull requests merge into the one below
  unless each is moved onto `main` first, since merged branches are not deleted here). Its release pull request sets
  `VERSION`; then staging, production, and the tag on H's word. 1.2.1 (`v1.2.1`) is what production ran before it
  **[stated: H, 2026-11-28]**.
- Four add migrations: #201 (`one_time_links.group_id`), #203 (`conversations.avatar_id`, notice 10 allowed), #204
  (`sticker_packs.everywhere`, `sticker_pack_chats`, `sticker_pack_people`) and #210 (`pg_trgm`, `gom_unaccent`, the
  trigram index on messages, then `ANALYZE messages`). #210's index reads every message once: seconds at Gom's size, about
  30 s for 10 million, past the bundle's 30 s command limit; a database that large needs `Command Timeout=600` on the
  bundle's connection string for that run **[verified: on a local copy with 10 million messages]**.
- Measured on a local copy with 10 million messages, 21 people sending 30 a second with their sockets open (#208 to #211,
  2026-11-28): sends 17 to 50 ms in the middle; a far search 7 to 25 ms (was 6.6 s); a trim of a million messages and a
  disband of 800,000 with no failed send and at most 21 connections. Still slow and left so: the owner's storage page
  (2.9 s) and trim previews (1 to 1.5 s), which count every message **[verified]**.
- PostgreSQL on staging and production now has 512 MB: at 256 MB the 10-million test got it killed for memory, at 512 MB
  not **[stated: H raised it, 2026-11-28; verified: the local test at both sizes]**.
- `main` at 1.3.0 passes 1,076 page tests and 1,186 server tests. Seen in Chrome at phone and desktop size on the local
  app: the welcome page, the owner's parts, refusing, the tester link with its group, sticker choices, a group picture
  and its line, the 1:1 header, the backgrounds, the privacy page's source section, and `/help` in both languages
  **[verified, 2026-11-28]**.
- The Android app changes in #200 (the fallback paths of owner notices) and #205 (the launcher and status-bar icons):
  it needs a build and an emulator pass before the next Play bundle **[verified: their diffs]**.
- Google Play: the first bundle (1.1.0) is built; H's developer account is still waiting for Google's identity check
  **[stated: H, 2026-11-27]**. How to find testers is in H's own outreach doc (not in the repository); how they get in is
  in `docs/testers.md`.
- GitHub's private vulnerability reporting is on (default settings), so `SECURITY.md`'s link works **[stated: H; verified:
  the API says `enabled: true`, 2026-11-28]**.

## 2. Work queue (agreed order)

Each is discussed first (design, decisions, what cannot be verified), then built.

1. **Release 1.3.0**: deploy the release commit to staging, then production; the tag only when H says so.
2. **The app's 1.3.0 build** (new icons, owner notice paths): staging and prod APKs on the build VM, the emulator pass
   (themed icon on, a notification's small icon), then the APK for the family and the Play bundle.
3. **The app on Google Play**, once Google has checked H's identity: the app created with Play's own signing key, the
   bundle in a closed test (12 testers for 14 days, `docs/testers.md`), the listing and declarations, then production.
   Uploading from Gradle with a service account limited to test tracks comes after.
4. **Share into Gom.** Gom registers as a share target in its manifest, so "Share" on a phone lists Gom and opens it
   with the link or text ready, then asks which conversation. Gom-only: Peekbox and Bloomy already call
   `navigator.share` **[stated: their sessions, 2026-11-25]**. Works only where Gom is installed **[inferred]**; to check
   against current docs before designing (GET or POST, text and links only or files too). The Android app needs its own
   share target too **[inferred]**.
5. **A webhook that posts into a group.** A secret link per group posts plain text as a labelled bot. What operations
   asked for: a plain-text POST, safe to send twice, a fast 2xx even when nobody is online, a rate limit per secret, and
   a secret that can be replaced without changing the group. Operations would use it only beside their own alerts (a
   digest of deploy results and a daily "all quiet" line), never instead, since Gom runs on a machine it would report
   on. Bloomy would send a new paid order, a delivery cut-off coming up, and a review to approve, beside its email
   **[stated: those sessions]**.
6. **Link cards.** Only for https addresses on a list in configuration (the shops' own sites), with a timeout, a size
   limit and a stored copy; title, picture and description, no price (a campaign price would go stale). Operations'
   check of what the container can reach on the host's own network comes first **[stated: operations]**.
   Link previews for any address were built once and removed in #61; their worker is in the git history.

Raised, not changed: a group's page says "Trưởng nhóm" while the chat panel says "Nhóm trưởng".

Not being built [H, 2026-11-25]: a cache for reads or message search. Measured instead (2026-11-28): reads were fast; what
was slow got its own fix (#208, #210, #211).

Later: a load test of the app at its 512 MB limit with several people uploading pictures and videos (the 2026-11-28 test
sent text only; the app peaked at about 220 MB); the next whole-repository review (`docs/repo-review.md`) at the end of
the queue.

## 3. Known limits

- An invite link is shown once; lost, it is revoked and made again.
- The trim notice says "Ảnh và tệp gửi trước …" whatever the filters were; each trimmed file shows its own placeholder.
- A page that receives a trim notice reloads that conversation from its newest page, so someone scrolled far up lands at
  the bottom.
- The `TrimMessages` migration's `Down` cannot restore the reply key once a trim has left an answer to a removed message.
- Notifications on Android pop up only after the site's notification category is set to show pop-ups; browsers create
  it at the default level **[stated: Chromium's behaviour, as researched on 2026-11-25]**.
- Notifications cannot work in a browser without a push service of its own: Helium and ungoogled-chromium removed
  Google's and have none (imputnet/helium#927 asks for Mozilla's). Gom says so after 20 seconds **[stated: Helium's issue
  tracker]**.
- After "delete for me" on a conversation, pins and reactions of the old messages stay for everyone else; they are only
  hidden for that person, as everything before the clear is.
- A request to someone who blocked you waits for good unless they unblock; then it shows to them like any other.
- Gboard's GIF button does not insert into Gom in Chrome, Brave or Firefox for Android [stated: H tried all three]. Chrome
  has an "IME media insertion" flag for it, but Brave does not offer it; the Android app can take the picture itself
  **[inferred]**.
- An open chat page learns of a deploy when its socket reconnects: about 30 s in a local test, since the reconnect waits
  grow while the server is down [verified]; other pages within ten minutes or when shown again. Pages loaded before #133
  send no page version, so `Pages.PageVersion` cannot force them; they still move over through #132.
- Files picked and not sent, and a change to a sent message, hold the reload by itself (the bar stays); typed words are
  kept only in the tab that typed them (`sessionStorage`).
- A video goes within 45 MB: the page lowers its bitrate, then drops to 480p, so about 8½ minutes with sound fit; a longer
  one, or one the device cannot convert that is over 45 MB, fails in the tray and has to be cut first.

- A voice message is a WAV file, about ten times the size the browser's own recorder makes (1.9 MB a minute): the
  recorder's WebM or fragmented MP4 has no length a browser shows and cannot be sought in.
- Sound sent before 1.2.0 stays a download: its type was stored as "file" when it came.
- Disappearing messages cannot reach notifications already shown on a phone, screenshots or forwarded copies (the panel
  says so). A message is deleted by the server within 30 s of its time; open pages take it away at its time themselves.
- Two-step sign-in has no backup codes (H): a lost phone needs the owner's reset link, or the server command for the
  owner. The key is stored as it is, not encrypted.
- Tapping play in the split second while a just-sent sound message turns from "sending" to sent does nothing; a second
  tap plays it.
- A search for a word of one or two letters still reads the whole conversation: the index works on three letters at a
  time. A jump back reaches at most 2,000 messages, the picture viewer 1,000 messages with pictures; past that the
  message is not shown.
- Disbanding a group of hundreds of thousands of messages takes about a minute: it is gone for everyone at once, but the
  leader's page may give up waiting and show an error. A server restart in that minute leaves it with no members, on
  the owner's storage page as an empty group to delete there **[verified: 800,000 messages, and a page that hung up
  after 2 s]**.

## 4. Not verified yet

- A real long press on a phone (the row menu's one second, and that Android shows no link menu of its own); headless
  Firefox has no hover, so Firefox's mouse path (the "⋯" under the pointer) was not seen either.
- Helium itself: the fix follows its issue tracker and fakes in tests; neither Helium nor Brave is installed here.
- The 62 new stickers on an iPhone, and on a slow phone line (the largest are about 60 KB).
- The storage page and the trim placeholders in Firefox and Safari (only Chromium-based browsers were used).
- That a picture still in a browser's one-day cache after a trim does not reappear.
- Videos beyond H's phone: Samsung Internet, iPhone Safari, HEVC from an iPhone, and how long a long video takes to
  convert on a slower phone (on this Linux PC's Chrome a 4-minute 1080p clip took 35 s, a 6⅔-minute one 57 s **[verified]**).
- Reloading by itself on a real phone: the checks hid the tab by overriding `visibilityState`; how soon a phone that has
  slept for hours notices a new version depends on when its browser wakes the tab. The minimum page version was checked
  in Chrome and Firefox only.
- Why one call in 50 between two apps failed after #171: one phone may have run out of its three tries for addresses, or
  no route between the phones worked. The failing phone's `adb logcat -d -s GomCall` tells which; H reports it if it
  happens again. The retry was seen only on the answering side, on an emulator (it fixed every empty first answer).
- Voice messages on Safari (Mac or iPhone) and inside the Android app: recorded only in Chrome and Firefox with fake
  microphones. Playwright's Linux WebKit has no `MediaRecorder`, so Gom rightly shows no microphone there.
- The caller's picture on the app's ringing screen: covered by tests only; it needs a real call through a server with
  #186.
- Gradients on an iPhone: Safari supports a background fixed to the screen only in part, so each message may show the
  whole gradient instead.
- Two-step sign-in with a real authenticator app on a phone (checked with codes computed in a script, and the QR code
  drawn), and `autocomplete="one-time-code"` offering the code from a notification.
- The owner's sticker packs on a slow phone line, and a large animated sticker's memory on an old phone.
- The background names on H's phone after #199: in Chrome at 390 px they fit on one line, so the wrapping case was not
  seen again.
- The owner's parts, the welcome page, group pictures and the sticker choices in Firefox and Safari (Chrome only).
- The new launcher and status-bar icons on a phone or the emulator, with Android's themed icons on and off.
