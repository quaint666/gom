# Next session: where things stand and what comes next

Written 2026-11-25, at the end of the session that ran the second whole-repository review and opened PRs #90 to #100. It
keeps the work queue agreed after #88 (section 2), which is unchanged. Claims are marked **[verified]** (seen in
the code or real output), **[stated]** (someone said so; who is given), or **[inferred]** (reasoned, not tried).

## 1. Where things stand

- **Open, to merge in this order** (each is based on the one before, so each diff shows only its own change):
  - #90 the emoji and sticker button inside the message field;
  - #91, #92 the conversation menu (server, page): pin, mark unread, mute, make a group with, hide, delete for me. **#91
    has a migration: take a backup dump before deploying it.**
  - #93 to #96 the second review's fixes: hand-made requests (NUL, unbound values, the page's error codes), races
    (recall against edit, sign-in bursts, the upload cleaner), privacy (a block that could be detected, Gom IDs for
    ex-friends), time limits (requests, stalled uploads, sockets whose session ran out);
  - #97 quieter menus (the row's "⋯" only on the open row and under a mouse, a one-second press, outside closes the
    picker); #98 110 stickers in six packs; #99 a browser with no push service gets words, not a spinner; #100 these docs.
- Every PR: all page and server tests pass (741 and 837 at #99), and each new test was checked by breaking the code on
  purpose **[verified]**. The menus, composer and picker were looked at in Chromium 157, Firefox 160 and WebKit 2.54
  **[verified]**.
- Earlier work (#79 to #89) is merged and in production **[stated: H]**. The forwarded-header chain was checked live on
  2026-11-25 **[verified: H's run]**. Backups mirror the uploads folder **[stated: operations]**.
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
