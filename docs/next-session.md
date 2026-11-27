# Next session: where things stand and what comes next

Written 2026-11-25, brought up to date on 2026-11-26 (after #133), 2026-11-27 (after #171) and 2026-11-28 (after #193).
Claims are marked **[verified]** (seen in the code or real output), **[stated]** (someone said so; who is given), or
**[inferred]** (reasoned, not tried).

## 1. Where things stand

- Release 1.1.0 is tagged (`v1.1.0`, #173). Merged since: the CI check of the app when `VERSION` changes (#174), the
  app's release build for Google Play (#175), and H's fourteen changes A to I (#177 to #186) and the group name box
  (#188).
- Open as a stack, to merge in this order: voice messages (#189, on `main`), two-step sign-in (#190), disappearing
  messages (#191), a card closing with a tap outside and the info panel sliding out (#192), the owner's sticker packs
  (#193), then this docs sweep with `VERSION` at 1.2.0. Each needs the one before it.
- Three of them add migrations: #190 (`users.two_step_key`, `two_step_last_step`), #191 (`messages.expires_at`,
  `conversations.disappear_seconds`, a check constraint widened) and #193 (`sticker_packs`, `custom_stickers`). The
  image runs its migration bundle before each start.
- Before this sweep, the stack passed 1,046 page tests and 1,129 server tests, and one pass in Chrome, Firefox and
  WebKit on the local app (voice messages recorded in Chrome and Firefox; WebKit's Linux build has no `MediaRecorder`)
  [verified, 2026-11-28].
- The Android app: an APK from #186 (the new ringing screen) was built on the build VM and checked on the emulator
  [verified]. Nothing in #189 to #193 changes the app, so that build covers 1.2.0 except its version number
  [verified: their diffs].
- Google Play: the first bundle (1.1.0) is built; H's developer account is still waiting for Google's identity check,
  so the app cannot be created in Play Console yet [stated: H, 2026-11-27].

## 2. Work queue (agreed order)

Each is discussed first (design, decisions, what cannot be verified), then built.

1. **Release 1.2.0** once the stack is merged: staging, then production; the tag only when H says so. Then the app's
   1.2.0 build (staging, prod, and the Play bundle) from the release commit.
2. **The app on Google Play**, once Google has checked H's identity: the app created with Play's own signing key, the
   bundle in a closed test (12 testers for 14 days), the listing and declarations, then production. Uploading from
   Gradle with a service account limited to test tracks comes after.
3. **Share into Gom.** Gom registers as a share target in its manifest, so "Share" on a phone lists Gom and opens it
   with the link or text ready, then asks which conversation. Gom-only: Peekbox and Bloomy already call
   `navigator.share` **[stated: their sessions, 2026-11-25]**. Works only where Gom is installed **[inferred]**; to check
   against current docs before designing (GET or POST, text and links only or files too). The Android app needs its own
   share target too **[inferred]**.
4. **A webhook that posts into a group.** A secret link per group posts plain text as a labelled bot. What operations
   asked for: a plain-text POST, safe to send twice, a fast 2xx even when nobody is online, a rate limit per secret, and
   a secret that can be replaced without changing the group. Operations would use it only beside their own alerts (a
   digest of deploy results and a daily "all quiet" line), never instead, since Gom runs on a machine it would report
   on. Bloomy would send a new paid order, a delivery cut-off coming up, and a review to approve, beside its email
   **[stated: those sessions]**.
5. **Link cards.** Only for https addresses on a list in configuration (the shops' own sites), with a timeout, a size
   limit and a stored copy; title, picture and description, no price (a campaign price would go stale). Operations'
   check of what the container can reach on the host's own network comes first **[stated: operations]**.
   Link previews for any address were built once and removed in #61; their worker is in the git history.

Raised, not changed: a group's page says "Trưởng nhóm" while the chat panel says "Nhóm trưởng".

Not being built [H, 2026-11-25]: a cache for reads or message search (measure first if anything ever feels slow; a trigram index is the first step for search).

Later: a load test at the app's 512 MB memory limit with several people uploading; the next whole-repository review (`docs/repo-review.md`) at the end of the queue.

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

## 4. Not verified yet

- A real long press on a phone (the row menu's one second, and that Android shows no link menu of its own); headless
  Firefox has no hover, so Firefox's mouse path (the "⋯" under the pointer) was not seen either.
- Helium itself: the fix follows its issue tracker and fakes in tests; neither Helium nor Brave is installed here.
- The 62 new stickers on an iPhone, and on a slow phone line (the largest are about 60 KB).
- The storage page and the trim placeholders in Firefox and Safari (only Chromium-based browsers were used).
- A trim on a large history: one statement per conversation is enough at family size **[inferred]**; not measured.
- That a picture still in a browser's one-day cache after a trim does not reappear.
- Videos beyond H's phone: Samsung Internet, iPhone Safari, HEVC from an iPhone, and how long a long video takes to
  convert on a slower phone (on this Linux PC's Chrome a 4-minute 1080p clip took 35 s, a 6⅔-minute one 57 s **[verified]**).
- Reloading by itself on a real phone: the checks hid the tab by overriding `visibilityState`; how soon a phone that has
  slept for hours notices a new version depends on when its browser wakes the tab. The minimum page version was checked
  in Chrome and Firefox only.
- Search in a conversation on a long history: one `ILIKE` per word with no index is enough at family size
  **[inferred]**; not measured.
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
