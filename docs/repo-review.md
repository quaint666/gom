# Full repository review (prompt)

A prompt for a whole-repository review, so each run checks the same things the same way. Run it at the end of a
milestone and before the first deployment. Paste everything below the line to the assistant in a session started in
this repository. When a feature is built that this prompt does not cover, add its area before the review that includes it.

**Not covered yet (not built):** sharing into Gom from other apps, the webhook that posts alerts into a group, link
cards, two-step sign-in for the owner, the load test (the end goal is 1,000 to 5,000 connections, although real traffic
will be far lower). Backups are run outside this repository; only what Gom asks of them is in scope
(`README.md`, "On a server").

---

Review the whole Gom repository: server, browser scripts, tests, docs and mockups. Report first and change nothing until
the owner approves. Then fix in small batches, one branch and pull request each, each shown as a diff first.

## What to look for

**The owner's list**
- Dead things: unused files, CSS classes, exports, packages, tests for code that is gone, local leftovers.
- Readability and maintainability: files that do too much, misleading names, comments that no longer match the code.
- Browser compatibility: every HTML, CSS and script feature in use, checked against current data (`web-features`), not
  memory, and what happens on a browser that lacks it.
- Implicit behaviour: anything that works by default instead of by intent (a route that answers by accident, a value
  that fails to bind and becomes a default, a fetch with no timeout, a missing status check).
- Drift between code, docs and mockups: `CLAUDE.md`, `README.md`, `docs/decisions.md`, `design/design-notes.md`.
- One source per value: an address, text, number or colour written in more than one place.

**Areas**
1. **Messages and ordering.** The per-conversation counter bumped in the same transaction as the insert; a duplicate
   send leaves no gap and no second row; fan-out only after commit; history, sync and unread agree (joined_seq,
   recalls, removals). Two sends at once, the same send twice, a recall racing a send.
2. **Sockets.** One bounded queue and one writer loop per socket; a slow client is closed (1013) and resyncs from its
   cursor; limits per user (and a "typing" frame over its pace is dropped, never an error or a close); Origin on the upgrade; membership checked on delivery, so a person removed from a group stops
   receiving at once; what a socket does when the session ends (4401) and when the database is down.
3. **Privacy.** The Gom ID appears only to self and friends; a sender is `{id, displayName}`; blocks hide; read receipts
   are one switch both ways, and "typing…" follows it; a mention names a member by the name the group already sees, never a Gom ID; a removed or left member sees nothing after. Search every response DTO for a field that
   leaks.
4. **Auth and sessions.** Cookie flags, Origin on every state change, no state change on GET, rate limits, what a locked
   or disabled account can still do, password and invite handling, session end on reset.
5. **Hand-made requests.** NUL in a cookie, query or body; the same request twice at once; a method the address does
   not take (405, HEAD); an id that does not parse; a body too large; a very long string. Run them as Production.
6. **Browser.** No `innerHTML` with data, the CSP, no `any`, focus after every change, keyboard use, IME composing,
   reduced motion, contrast in both themes, accessible names, outbox behaviour (ordered, retried, cleared on sign-out),
   an older answer never overwriting a newer one.
7. **Performance.** Queries per request (none inside a loop), indexes for every filter and order used, payload sizes,
   the unread and list queries on a large history.
8. **Tests.** Real PostgreSQL per class; a test for each rule above; for every fix, break the code on purpose and
   confirm a test fails; a break no test catches is a finding.
9. **Security scanning and dependencies.** Open CodeQL alerts, vulnerable NuGet or npm packages, workflow actions
   pinned by commit.
10. **Repository.** README setup works from a clean clone; migrations apply on an empty database and on the previous
    schema; decisions in `docs/decisions.md` match what is built.
11. **Pictures and files.** A picture is decoded, made upright, scaled and re-encoded so no metadata survives; anything
    else is only ever a download with a safe type; size, pixel and per-person limits; who may fetch what (the uploader
    before it is in a message, then members who were in the conversation when it was said and did not hide it); a file's
    bytes deleted only after its row's commit; the cleanup of uploads nobody sent.
12. **Notifications.** The RFC 8291 encryption against the RFC's worked example; a notification carries no more than its
    device chose (the words, the names, or only "Tin nhắn mới"), and a message taken back replaces it;
    who is pushed (not the sender, not muted unless named in a mention and not blocking the writer, not removed, not someone who joined or cleared after it; a device with Gom open
    is pushed too, by H's choice); a 404 or 410 deletes the subscription; a slow push service never delays a send; only
    known push hosts are accepted; a browser with no push service (Helium) gets words, not a switch that never finishes.
13. **Owner tools and deletion.** Deleting your own account (what stays, what goes, the leader handing over); the storage
    view never naming who talks to whom in one-to-one chats; trims: the preview equals what is removed, "Delete" typed
    exactly, a notice in each conversation, trimmed files never fetched or forwarded again, answers to trimmed messages
    kept.
14. **The conversation menu.** Each switch is one person's own (nobody else's list changes); "delete for me" hides
    everything before it everywhere that person can reach a message (history, sync, search, pins, media, quotes,
    reactions, recall, live updates, files); a mark unread never moves the read position; pinned order; the "⋯" only on
    the open row and under a mouse; the one-second press, and the click that ends it.
15. **Stickers.** The page's packs, the server's list and the files agree; no sticker ever offered is dropped (sent ones
    must still draw); each SVG is upstream's, with no script or outside reference.
16. **Settings and looks.** Each part's address, the owner's parts refused to a member by the server as well as the page;
    every colour's text pairs at 4.5 to 1 or better in both themes; backgrounds readable under grey text; a redrawn tile
    under a new `?v=`.
17. **Versions.** The socket's "ready" names the version the pages are built from; a page reloads by itself only when
    nothing would be lost, and never in a loop; typed words kept per person in `sessionStorage` only; `Pages.PageVersion`
    raised for a change older pages cannot use, and what they send meanwhile kept in the outbox.
18. **Videos and requests to join.** A video streamed to disk within its limit, kept as sent, with the sender's still
    picture; a request to join seen only by the owner until approved, and a waiting account able to do nothing else.

**Deployment items** (keep listing until fixed): forwarded headers (`ForwardedHeaders__Hops` matches the proxies in
front; checked live on 2026-11-25), reverse proxy WebSocket upgrade, the proxy's request body limit for files,
`AllowedHosts`, a health endpoint, log level and what the logs contain, data-protection keys, migrations before start,
the uploads backup copied as a mirror.

## How to check

- Search before claiming: every file, class, export and package against its users (`grep -rn`).
- Probe the published build (`dotnet publish -c Release`), not `dotnet run`: GET, HEAD, POST and PUT on each important
  address, once with `Accept-Encoding: gzip, br`; the WebSocket with a wrong Origin, no cookie, binary and oversized
  frames.
- Test real browsers: Chromium over CDP with a private profile; Firefox over WebDriver BiDi (`--remote-debugging-port`,
  then `ws://…/session`); WebKit through WebKitWebDriver with MiniBrowser. Say which were not run. Never `pkill`, by any
  name: `pkill -f` matched the shell running it in the second review. Stop the PIDs you started.
- WebKit keeps no `Secure` cookie over plain http, even on localhost, so the session cookie never sticks: serve the
  published build on HTTPS too (`dotnet dev-certs https -ep <scratch>/dev.pfx`, `Kestrel__Certificates__Default__Path`)
  and let the WebDriver session accept it (`acceptInsecureCerts`). MiniBrowser has no headless mode: run
  WebKitWebDriver under `xvfb-run -a` with `LIBGL_ALWAYS_SOFTWARE=1`, in its own process group.
- Playwright's own WebKit is the easier way when it runs: its build needs Ubuntu 24.04 libraries, so on another
  distribution run it in an Ubuntu 24.04 container, over HTTPS for the same cookie reason.
- Headless Firefox reports no hover device, so it takes the phone path of every `(hover: hover) and (pointer: fine)`
  rule; say so rather than reading it as a bug.
- Races need several requests at once (8 to 30), from the published build and in a test: the second review found a
  deadlock (recall against edit) and a sign-in slowdown that a burst walked past only that way.
- Support claims come from data (`npm pack web-features`), not memory.
- Mark every claim `[verified]` (seen in real output this session), `[inferred]` or `[stated]`. Say plainly what was not checked.

## The report

1. **Summary:** a score out of 10, the top 3 fixes, and what must happen before any deployment.
2. **Findings by area:** severity (Critical / Warning / Suggestion), file and line, the problem, the evidence, the fix.
3. **Order of work:** numbered batches, one pull request each, smallest risk first: dead code, bugs, one source per
   value, CSS, docs, deployment.
4. **Decisions for the owner:** anything that deletes content, changes an address, or changes how the project runs.
5. **Good habits** the project does not have yet.

## Found in the second review (2026-11-25): confirm they stay fixed

Run by the assistant with H on the code after #89, with Peekbox's prompt alongside. Score 7.5/10: the core rules held
(numbering under a lock, fan-out after commit, membership on delivery, re-encoded pictures); the defects were in
hand-made requests and requests at the same moment. Each fix has a test, named in brackets.

- A NUL in a search, the sign-in ID or any body naming a Gom ID reached PostgreSQL: a 500 (`HandMadeRequestTests`).
- `limit=abc` on history was a quiet 50 (`HandMadeRequestTests`; `MessageTests` had pinned the old 50).
- Recall locked the message before the conversation; with an edit at the same moment, 15 of 15 pairs deadlocked
  (`RaceTests`).
- The sign-in slowdown counted after the hash: 30 guesses at once had 18 checked (`RaceTests`, `SignInThrottleTests`).
- The upload cleaner deleted files before its rows and could delete an upload a send had just taken (`RaceTests`).
- A request to someone who blocked you said "pending" but stored nothing, so the block could be seen (`FriendTests`).
- The conversation list gave a one-to-one chat's Gom ID to ex-friends (`ConversationTests`).
- No time limit on the page's requests or uploads, and a socket outlived a session that ran out (`api.test.ts`,
  `SocketSessionTests`).
- Two server error codes had their own words but reached the page as "unknown" (`api.test.ts`).
- Asked for during the review and built: the conversation menu (#91, #92), quieter menus (#97), sticker packs (#98),
  and words for a browser with no push service (#99).
- Left, as H decided: HSTS is the proxy's (not checked here); the local .NET runtime (10.0.13) is behind the image's
  (10.0.15), a machine package for H to update.

