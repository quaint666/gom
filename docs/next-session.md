# Next session: where things stand and what comes next

Written 2026-11-25, at the end of the session that built PRs #79 to #88. It replaces the plan of 2026-11-24, which is now
built (Web Push, mute durations, stickers instead of GIFs, account deletion). Claims are marked **[verified]** (seen in
the code or real output), **[stated]** (someone said so; who is given), or **[inferred]** (reasoned, not tried).

## 1. Where things stand

- Merged, and in production by H on 2026-11-25 **[stated: H]**:
  - #79: the chat fills the screen on a phone (header and composer always reachable), the message field shrinks back,
    an offline page instead of an endless load, notifications kept by the push service for 28 days.
  - #80 to #82: 48 still stickers (Noto Emoji, SIL OFL 1.1) and an emoji tab; one to three emoji alone are drawn large.
  - #83 and #84: deleting your own account (what you said stays, under "Tài khoản đã xóa").
  - #85, #86, #88: the owner's storage page; deleting a group nobody is in; trimming old pictures and files (a
    placeholder stays) and old messages (newer answers stay). #87 made a page test independent of the time zone.
- Checked on H's phone: pop-up notifications (after turning on "Hiển thị dạng cửa sổ pop-up" in the site's Android
  notification category), the storage page, the full-height chat **[stated: H]**.
- Backups **[stated: operations]**: both copies of the uploads folder are now mirrors, guarded against a missing or
  empty source, so deletions and trims leave the backups; the restore check skips rows with `removed_at`. Database dumps
  and machine snapshots keep deleted data until they age out, about a week at most.
- The forwarded-header chain in production works: forged `X-Forwarded-For` entries are ignored and two networks are seen
  as two addresses **[verified: H's run of the check script, 2026-11-25]**.
- Peekbox production is on hold until it has a real payment method **[stated: H]**.

## 2. Work queue (agreed order)

Each is discussed first (design, decisions, what cannot be verified), then built.

1. **Share into Gom.** Gom registers as a share target in its manifest, so "Share" on a phone lists Gom and opens it
   with the link or text ready, then asks which conversation. Gom-only: Peekbox and Bloomy already call
   `navigator.share` **[stated: their sessions, 2026-11-25]**. Works only where Gom is installed **[inferred]**; to check
   against current docs before designing (GET or POST, text and links only or files too).
2. **A webhook that posts into a group.** A secret link per group posts plain text as a labelled bot. What operations
   asked for: a plain-text POST, safe to send twice, a fast 2xx even when nobody is online, a rate limit per secret, and
   a secret that can be replaced without changing the group. Operations would use it only beside their own alerts (a
   digest of deploy results and a daily "all quiet" line), never instead, since Gom runs on a machine it would report
   on. Bloomy would send a new paid order, a delivery cut-off coming up, and a review to approve, beside its email
   **[stated: those sessions]**.
3. **Link cards.** Only for https addresses on a list in configuration (the shops' own sites), with a timeout, a size
   limit and a stored copy; title, picture and description, no price (a campaign price would go stale). Operations'
   check of what the container can reach on the host's own network comes first **[stated: operations]**.
   Link previews for any address were built once and removed in #61; their worker is in the git history.
4. **Two-step sign-in for the owner (TOTP).** Bloomy's design is the reference: `totp.ts` (the maths, the secret
   encrypted at rest) and `routes/admin-auth.ts` (a code used once per step, recovery codes, an attempt lock)
   **[stated: the Bloomy session]**.

Later: a load test at the 256 MB limit with several people uploading; a whole-repository review (`docs/repo-review.md`).

## 3. Known limits

- An invite link is shown once; lost, it is revoked and made again.
- The trim notice says "Ảnh và tệp gửi trước …" whatever the filters were; each trimmed file shows its own placeholder.
- A page that receives a trim notice reloads that conversation from its newest page, so someone scrolled far up lands at
  the bottom.
- The `TrimMessages` migration's `Down` cannot restore the reply key once a trim has left an answer to a removed message.
- Notifications on Android pop up only after the site's notification category is set to show pop-ups; browsers create
  it at the default level **[stated: Chromium's behaviour, as researched on 2026-11-25]**.

## 4. Not verified yet

- The storage page and the trim placeholders in Firefox and Safari (only Chromium-based browsers were used).
- A trim on a large history: one statement per conversation is enough at family size **[inferred]**; not measured.
- That a picture still in a browser's one-day cache after a trim does not reappear: the page shows the placeholder, but
  the cached copy itself was not looked at.
