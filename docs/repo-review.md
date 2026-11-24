# Full repository review (prompt)

A prompt for a whole-repository review, so each run checks the same things the same way. Run it at the end of a
milestone and before the first deployment. Paste everything below the line to the assistant in a session started in
this repository. When a feature is built that this prompt does not cover, add its area before the review that includes it.

**Not covered yet (not built):** push notifications, file and image uploads (storage), the ops-room bot, backups, the
load test (the end goal is 1,000 to 5,000 connections, although real traffic will be far lower).

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
   cursor; limits per user; Origin on the upgrade; membership checked on delivery, so a person removed from a group stops
   receiving at once; what a socket does when the session ends (4401) and when the database is down.
3. **Privacy.** The Gom ID appears only to self and friends; a sender is `{id, displayName}`; blocks hide; read receipts
   are one switch both ways; a removed or left member sees nothing after. Search every response DTO for a field that
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

**Deployment items** (keep listing until fixed): forwarded headers (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`), reverse
proxy WebSocket upgrade, `AllowedHosts`, a health endpoint, log level and what the logs contain, data-protection
keys, migrations before start.

## How to check

- Search before claiming: every file, class, export and package against its users (`grep -rn`).
- Probe the published build (`dotnet publish -c Release`), not `dotnet run`: GET, HEAD, POST and PUT on each important
  address, once with `Accept-Encoding: gzip, br`; the WebSocket with a wrong Origin, no cookie, binary and oversized
  frames.
- Test real browsers: Chromium over CDP with a private profile; Firefox and WebKit as far as the machine allows. Say
  which were not run. Never `pkill` a browser: stop the PIDs you started.
- Support claims come from data (`npm pack web-features`), not memory.
- Mark every claim `[verified]` (seen in real output this session), `[inferred]` or `[stated]`. Say plainly what was not checked.

## The report

1. **Summary:** a score out of 10, the top 3 fixes, and what must happen before any deployment.
2. **Findings by area:** severity (Critical / Warning / Suggestion), file and line, the problem, the evidence, the fix.
3. **Order of work:** numbered batches, one pull request each, smallest risk first: dead code, bugs, one source per
   value, CSS, docs, deployment.
4. **Decisions for the owner:** anything that deletes content, changes an address, or changes how the project runs.
5. **Good habits** the project does not have yet.
