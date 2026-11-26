# Next session: where things stand and what comes next

Written 2026-11-25, after the session that opened PRs #90 to #100, and brought up to date on 2026-11-26, after #111 to
#133. Claims are marked **[verified]** (seen in the code or real output), **[stated]** (someone said so; who is given),
or **[inferred]** (reasoned, not tried).

## 1. Where things stand

- Everything up to #131 is merged and deployed [stated: H, 2026-11-26]. Since #109: one source per value (#111), every
  piece of text in one table (#113, #119), the information page on phones (#114), notifications with words (#115),
  requests to join (#116, #117), English (#118), GIFs, pure black and a daily upload cap (#120), a fix for going to a
  message from the phone's information page (#121), search as you type across every conversation (#122), videos (#123,
  #124, #126), sixteen colours and fifteen backgrounds with "tossed" motif tiles (#127, #130), Settings as a menu with
  an address per part (#128, #130), and the owner page's polish (#131). Their decisions are in `docs/decisions.md`.
- Open, stacked in this order: #132 (open pages move to a new version, typed words kept across a reload, "Tải lại Gom"
  in Settings), #133 (a minimum page version, `Pages.PageVersion`), and the docs sweep after them.
- Videos play on H's phone after the deploy [stated: H, 2026-11-26]. The proxy in front allows 50 MB on the upload
  address for them [stated: the operations session, 2026-11-26].
- H has a Google Play developer account (personal) [stated: H, 2026-11-25].
- WebKit is tested with Playwright's own build, which needs Ubuntu 24.04 libraries: here it runs in an Ubuntu 24.04
  Distrobox, over https (a `__Host-` cookie needs it) [verified, 2026-11-26].

## 2. Work queue (agreed order)

Each is discussed first (design, decisions, what cannot be verified), then built.

1. **Voice calls and the Android app** (the next conversation starts here [stated: H, 2026-11-26]). The plan, its
   decisions, the Cloudflare and Android facts, what H does and the PR order are in **`docs/voice-calls.md`**; read it
   first. Its PR 1 went as three stacked pull requests (2026-11-26): 1a the call mockups (screens 21 to 30) with pull down
   to reload on the chat list, 1b the `/privacy` page in both languages, 1c its contact form. PR 2 is split: 2a the server's
   calls (the pull request after #138), 2b the call lines on the web (both open or merged by now). Next: PR 3, the relay (Cloudflare TURN logins). `Site__Operator` is set on the server [stated: the
   operations session, 2026-11-26].
2. **Share into Gom.** Gom registers as a share target in its manifest, so "Share" on a phone lists Gom and opens it
   with the link or text ready, then asks which conversation. Gom-only: Peekbox and Bloomy already call
   `navigator.share` **[stated: their sessions, 2026-11-25]**. Works only where Gom is installed **[inferred]**; to check
   against current docs before designing (GET or POST, text and links only or files too).
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
5. **Two-step sign-in for the owner (TOTP).** Bloomy's design is the reference: `totp.ts` (the maths, the secret
   encrypted at rest) and `routes/admin-auth.ts` (a code used once per step, recovery codes, an attempt lock)
   **[stated: the Bloomy session]**.

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
- Search across every conversation on a long history: one `ILIKE` per word with no index is enough at family size
  **[inferred]**; not measured.
