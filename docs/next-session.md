# Next session: where things stand and what comes next

Written 2026-11-25, brought up to date on 2026-11-26 (after #133), 2026-11-27 (after #171) and 2026-11-28 (after #193,
and again for release 1.3.0, for the 1.4.0 stack, for what H added to it, and after the 1.4.0 release, for 1.5.0 and for the group-panel stack, on 2026-11-29).
Claims are marked **[verified]** (seen in the code or real output), **[stated]** (someone said so; who is given), or
**[inferred]** (reasoned, not tried).

## 1. Where things stand

- Release 1.4.0 is out (2026-11-29): `v1.4.0` is tagged on the merge of #232 (`6ca2d06`), published on GitHub, deployed to
  staging and production, and `gom-1.4.0.apk` (a production-flavour debug build, emulator pass clean) is the only APK left
  on Copyparty **[stated: H, 2026-11-29]**. It holds #216 to #232: the stylesheet split into files, a reaction at its
  bubble's edge, double tap to heart, swipe to reply, "is typing", mentions, the announcement channel ("Thông báo"),
  "Có gì mới", the copy toast and the group avatar that opens a card; migrations `Mentions` and `Announcements`. It
  passes 1,147 page tests and 1,252 server tests. Release 1.3.0 (#199 to #211, `v1.3.0`) ran in production before it.
- Release 1.5.0 (#233 and #234, both merged 2026-11-29; `403d503` is the merge of #234) is two looks: reactions drawn as Messenger does (a round badge
  on the bubble's bottom corner) and the call screens without the old "Gom" label and logo, on the web and in the app
  (`CallScreen.kt`, so it needs `gom-1.5.0.apk`). H chose to release it alone so the app's call screen updates
  **[stated: H, 2026-11-29]**. Seen in Chrome at phone width only. `gom-1.5.0.apk` is built (versionCode 10500; emulator
  pass clean: no crash in logcat) and uploaded **[verified]**. `v1.5.0` is tagged on `403d503` and published on GitHub (2026-11-29, after H deployed).
- Release 1.6.0 (#235 to #241, all merged; `v1.6.0` on `0b3ddec`, published, deployed to staging and production by H;
  `gom-1.6.0.apk` built, emulator launch clean: versionCode 10600, no crash, sign-in screen; it is the only APK on
  Copyparty **[stated: H deleted the older ones]**) holds, from H's list: phone menus are list sheets (#235); a
  friend's menu opens from a long press or right-click and offers "Nhắn tin" and "Tạo nhóm với …" (#236); the 1:1
  header's picture opens the card, a group's name and picture open its information, the people icon is gone, and the info
  page slides in and out (#237); a group's information is edited in place, in the panel, 340 px wide (#238); "Quản lý
  nhóm", disbanding and the deletion of the `/groups/{id}` page (#239); docs (#240) and the release (#241). Seen in Chrome at
  desktop and phone width on a local app **[verified]**; web changes, so the WebView loads them without a new APK
  **[inferred]**; not seen in the app. 1,138 page tests and 1,247 server tests pass.
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

0. Nothing is waiting from the releases: 1.5.0 and 1.6.0 are out. Release notes for a version after 1.6.0 start from the
   `v1.6.0` tag; the build VM is started and stopped with vmctl, and H deletes older APKs by hand.
1. **The app on Google Play**, once Google has checked H's identity: the app created with Play's own signing key, the
   bundle in a closed test (12 testers for 14 days, `docs/testers.md`), the listing and declarations, then production.
   Uploading from Gradle with a service account limited to test tracks comes after.
2. **Share into Gom.** Gom registers as a share target in its manifest, so "Share" on a phone lists Gom and opens it
   with the link or text ready, then asks which conversation. Gom-only: Peekbox and Bloomy already call
   `navigator.share` **[stated: their sessions, 2026-11-25]**. Works only where Gom is installed **[inferred]**; to check
   against current docs before designing (GET or POST, text and links only or files too). The Android app needs its own
   share target too **[inferred]**.
3. **A webhook that posts into a group.** A secret link per group posts plain text as a labelled bot. What operations
   asked for: a plain-text POST, safe to send twice, a fast 2xx even when nobody is online, a rate limit per secret, and
   a secret that can be replaced without changing the group. Operations would use it only beside their own alerts (a
   digest of deploy results and a daily "all quiet" line), never instead, since Gom runs on a machine it would report
   on. Bloomy would send a new paid order, a delivery cut-off coming up, and a review to approve, beside its email
   **[stated: those sessions]**.
4. **Link cards.** Only for https addresses on a list in configuration (the shops' own sites), with a timeout, a size
   limit and a stored copy; title, picture and description, no price (a campaign price would go stale). Operations'
   check of what the container can reach on the host's own network comes first **[stated: operations]**.
   Link previews for any address were built once and removed in #61; their worker is in the git history.

Not being built [H, 2026-11-25]: a cache for reads or message search. Measured instead (2026-11-28): reads were fast; what
was slow got its own fix (#208, #210, #211).

Later: a load test of the app at its 512 MB limit with several people uploading pictures and videos (the 2026-11-28 test
sent text only; the app peaked at about 220 MB); the next whole-repository review (`docs/repo-review.md`) at the end of
the queue.

## 3. Known limits

- "Typing…" is only a hint: a page that is behind misses it, it needs both people to share their reading (the
  read-receipts switch), and a typing frame over the server's pace is dropped quietly.
- A mention is "@" and the member's name as written when it was sent: a member who changed their name since, or left,
  shows as plain words (the chat still lists it as a mention for the person it named). Two members with the same name are
  told apart by the id the sender chose, not by the words.
- `@all` rings no one through a mute; only a named member does (not if they blocked the writer). Adding `@all` in an
  edit tells no one.
- The mention arrow sits at the top corner of the thread, not the bottom as the mockup had it.
- The announcement channel is made at start (once there is an owner) and joined as each person becomes a member; if the
  database is not migrated yet the start only logs it and the next start makes it, so apply the migrations before the
  first start. Only the owner writes there (`owner_only`), a person waiting for approval or a deleted account is not in
  it, a member cannot leave or hide it, and it is not a group (no members list, no group page). Its title is the
  reader's language ("Thông báo" / "Announcements"), given by the page and by the notification text, not stored.
- "Có gì mới" lists releases written by hand in the strings: a release that forgets its entry shows an older "latest" in
  Settings. The release pull request carries it (`docs/releases.md`).
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

- The group panel, the list sheets and the friends' long press on a real phone and in the app's WebView; the panel between
  900 and 1199 px (the info page now shows there); the handle on a sheet is drawn, not draggable; a picture chosen from a
  phone's own picker for a group; a request waiting in "Quản lý nhóm" and a ban were covered by tests, not seen in a
  browser. Seen: Chrome at 1500 and 390 px wide (rest, members, the "⋯" menu, the add dialog, "Quản lý nhóm", the
  disband question, and a 404 at `/groups/{id}`).
- The reaction badge on a phone's own browser and in the app's page, with three or more reactions wrapping, and the call screen
  without its label on the emulator (the app's change is untested; the web's is seen in Chrome only).
- The announcement channel and "Có gì mới" on a real phone, and an announcement's notification (its title and the owner's
  words); seen: Chrome at phone and desktop widths as a member and as the owner, against a real server.
- Swipe to reply, double tap, the "@" list with a phone's keyboard, and a mention's notification on a real phone. Seen:
  real touch events in Chrome (CDP) and an iPhone profile in WebKit, two browsers live for typing and mentions; headless
  Firefox on a computer has no touch, so its swipe was not seen. Both gestures were written to need no APK.
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
