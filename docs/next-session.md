# Next session: where things stand and what comes next

Written 2026-11-25, after the session that opened PRs #90 to #100, and brought up to date the same day after #110, when
H put the web app's work before voice calls (section 2). Claims are marked **[verified]** (seen in
the code or real output), **[stated]** (someone said so; who is given), or **[inferred]** (reasoned, not tried).

## 1. Where things stand

- Everything up to #109 is merged and deployed, with no errors reported so far [stated: H, 2026-11-25]. Since #100:
  hidden conversations can be found and reopened (#102 to #104), a live sticker's preview (#105), the stickers'
  licence is Apache 2.0 (#106), a "Cờ" pack and four theme packs (#107, #108; #107 has a migration widening
  `messages.sticker` to 48), and 13 flags left out of the panel (#109): 432 stickers in eleven packs.
- H has a Google Play developer account (personal) [stated: H, 2026-11-25].

## 2. Work queue (agreed order)

Each is discussed first (design, decisions, what cannot be verified), then built. Items 1 to 6 were agreed on
2026-11-25 and opened as stacked pull requests #111 to #118 (merge in that order); their decisions are in `docs/decisions.md`.

1. **One source per value**: one page shell, the server's limits in `data-limits`.
2. **Every piece of text in one table** (two pull requests), words unchanged; tests keep their literal Vietnamese.
3. **A conversation's information on phones**: the panel as a full page, the actions at its foot.
4. **Notifications with the message's words**, the default for new devices; a message taken back replaces its own.
5. **Request to join**: sign-up open when the owner switches it on, a waiting screen, approve or refuse.
6. **English**: a second table, a setting per person, en-GB dates.
7. **Voice calls**, then the Android app. The plan, its decisions, the Cloudflare and Android facts, what H does and
   the PR order are in **`docs/voice-calls.md`**; read it first. Next step: its PR 1 (mockups and the `/privacy`
   page), shown to H before it is built.
8. **Share into Gom.** Gom registers as a share target in its manifest, so "Share" on a phone lists Gom and opens it
   with the link or text ready, then asks which conversation. Gom-only: Peekbox and Bloomy already call
   `navigator.share` **[stated: their sessions, 2026-11-25]**. Works only where Gom is installed **[inferred]**; to check
   against current docs before designing (GET or POST, text and links only or files too).
9. **A webhook that posts into a group.** A secret link per group posts plain text as a labelled bot. What operations
   asked for: a plain-text POST, safe to send twice, a fast 2xx even when nobody is online, a rate limit per secret, and
   a secret that can be replaced without changing the group. Operations would use it only beside their own alerts (a
   digest of deploy results and a daily "all quiet" line), never instead, since Gom runs on a machine it would report
   on. Bloomy would send a new paid order, a delivery cut-off coming up, and a review to approve, beside its email
   **[stated: those sessions]**.
10. **Link cards.** Only for https addresses on a list in configuration (the shops' own sites), with a timeout, a size
   limit and a stored copy; title, picture and description, no price (a campaign price would go stale). Operations'
   check of what the container can reach on the host's own network comes first **[stated: operations]**.
   Link previews for any address were built once and removed in #61; their worker is in the git history.
11. **Two-step sign-in for the owner (TOTP).** Bloomy's design is the reference: `totp.ts` (the maths, the secret
   encrypted at rest) and `routes/admin-auth.ts` (a code used once per step, recovery codes, an attempt lock)
   **[stated: the Bloomy session]**.

Not being built [H, 2026-11-25]: a cache for reads or message search (measure first if anything ever feels slow; a trigram index is the first step for search).

Later: a load test at the 256 MB limit with several people uploading; the next whole-repository review (`docs/repo-review.md`) at the end of the queue.

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

## 4. Not verified yet

- A real long press on a phone (the row menu's one second, and that Android shows no link menu of its own); headless
  Firefox has no hover, so Firefox's mouse path (the "⋯" under the pointer) was not seen either.
- Helium itself: the fix follows its issue tracker and fakes in tests; neither Helium nor Brave is installed here.
- The 62 new stickers on an iPhone, and on a slow phone line (the largest are about 60 KB).
- The storage page and the trim placeholders in Firefox and Safari (only Chromium-based browsers were used).
- A trim on a large history: one statement per conversation is enough at family size **[inferred]**; not measured.
- That a picture still in a browser's one-day cache after a trim does not reappear.
