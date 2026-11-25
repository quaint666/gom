# Gom
A small invite-only web chat for family and friends, replacing Zalo, with message history on the server. ASP.NET Core 10 (Kestrel) with raw WebSockets (no SignalR), PostgreSQL 19, EF Core 10 + Npgsql; in the browser, framework-free TypeScript plus lit-html, multi-page (from M1). UI text is Vietnamese; code, docs and chat are English.

## Local setup
See `README.md`. `dotnet test` needs `ConnectionStrings__Default` pointing at a PostgreSQL 19 server.

## Rules
- This repo is public: no secrets, hostnames, IPs, home-lab details or real people's data in any file or commit.
- Every environment-specific value (connection string, base URL, keys, allowed origins, storage path) comes from configuration; no hardcoded value.
- One source per value (a constant, a setting, the page shell's limits, one shared function); never repeat a hardcoded value. A limit the server holds reaches the page through `data-limits`, never as a copy. The one exception: tests write expected values out, so a changed value fails a test.
- Warnings that mean a bug are build errors (`Directory.Build.props`). A new warning is fixed, not suppressed.
- Messages: one `last_seq` counter per conversation, bumped by `UPDATE ... RETURNING` in the same transaction as the insert; unique `(sender_id, client_id)` for idempotent sends; acknowledge and fan out only after COMMIT. A duplicate send must roll back its counter bump or look up first. The database is the truth; live delivery is a convenience.
- Sockets: one bounded queue and one writer loop per socket; a slow client is closed and resyncs from its cursor.
- Auth: cookie `__Host-` prefix, `HttpOnly`, `Secure`, `SameSite=Lax`; an Origin check on every state-changing request and on the WebSocket upgrade; no state change on GET; membership checked on every send and on delivery.
- Queries: LINQ by default; interpolated `FromSql` / `ExecuteSql` only for atomic steps; never string-built SQL.
- No direct edits to `main`: one branch and one pull request per change, about 1,500 changed lines or fewer, not counting images and fonts (say why if more). Decisions are one line in `docs/decisions.md`.

## Docs
- `docs/decisions.md`: what was decided, and why.
- `docs/next-session.md`: where things stand and what comes next.
- `docs/repo-review.md`: the prompt for a whole-repository review.
- `design/`: the mockups (images and HTML sources) and `design-notes.md`, the look and the product rules the screens assume.
