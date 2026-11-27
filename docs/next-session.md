# Next session: where things stand and what comes next

Written 2026-11-25, brought up to date on 2026-11-26 (after #133) and on 2026-11-27 (after #171). Claims are marked
**[verified]** (seen in the code or real output), **[stated]** (someone said so; who is given), or **[inferred]**
(reasoned, not tried).

## 1. Where things stand

- Everything up to #171 is merged. Release 1.0.0 is tagged (`v1.0.0`, everything to #150). Staging and production run
  #170's merge; #171 changed only the Android app [verified: its diff], so the servers need nothing for it.
- Since #133: the docs sweep (#134), the privacy page and its contact form (#138), the server's calls, the relay and FCM
  (#140 to #145), the Android app's shell (#150), the release number in one place (#152, #154), GIFs from the keyboard in
  the app (#153), calls on the web (#155 to #160), calls in the app (#165 to #169), a call that never connects ending
  after 30 s with `GomCall` log lines (#170), and never sending a first offer or answer with no addresses (#171). Their
  decisions are in `docs/decisions.md`; the call plan and its PR order are in `docs/voice-calls.md`.
- Calls on real phones after #171: 49 of 50 calls between two apps connected; the one failure ended by itself after 30 s
  [stated: H, 2026-11-27]. Calls between a browser and the app connect every time [stated: H]. The speaker works on a
  Samsung phone with Android 14 where it did not before #169 [stated: H].
- The app is a debug build, installed by hand; there is no release signing and nothing on Play yet. It still says 1.0.0:
  the calls go out with the next release.
- H has a Google Play developer account (personal) [stated: H, 2026-11-25].
- WebKit is tested with Playwright's own build, which needs Ubuntu 24.04 libraries: here it runs in an Ubuntu 24.04
  Distrobox, over https (a `__Host-` cookie needs it) [verified, 2026-11-26].

## 2. Work queue (agreed order)

Each is discussed first (design, decisions, what cannot be verified), then built.

1. **Release 1.1.0** (calls on the web and in the app), as `docs/releases.md` says: a pull request changing `VERSION`
   with its entry under "What's new", staging, then production; the tag only when H says so. Production has calls on,
   the relay key and FCM, but not `Calls:AppsTakeCalls`, so its calls do not ring the app yet [stated: operations,
   2026-11-27]; turning it on is operations' change, on H's word.
2. **The app for everyone** (PR 7 of `docs/voice-calls.md`): release signing (decision 39: Play App Signing, H keeps the
   upload key), a Play build, internal testing first, the listing, the declarations (decision 41) and the update check.
   An app installed by hand is signed with another key, so moving to Play's means uninstalling it once.
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
6. **Two-step sign-in for the owner (TOTP).** Bloomy's design is the reference: `totp.ts` (the maths, the secret
   encrypted at rest) and `routes/admin-auth.ts` (a code used once per step, recovery codes, an attempt lock)
   **[stated: the Bloomy session]**.

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
