# Decisions

One line each: the decision, then the reason.

- 2026-11-24: The app is called Gom ("to gather"). A plain Vietnamese word, short, usable as a repo name and a subdomain.
- 2026-11-24: ASP.NET Core 10 with raw WebSockets, not SignalR. Learning the protocol is part of the goal; same stack as Peekbox.
- 2026-11-24: Separate service and repository from the shops, with its own accounts. The shops only send signed HTTP calls and plain links.
- 2026-11-24: PostgreSQL 19, its own instance, EF Core 10 with Npgsql. Peekbox staging already runs 19; CI tests 19 from the first commit.
- 2026-11-24: Messages are an ordered event stream per conversation (created, deleted; edits and reactions later), with a materialized messages table. Order comes from a per-conversation counter row, not a global sequence, so commit order cannot differ from sequence order.
- 2026-11-24: Browser code is framework-free TypeScript with lit-html, multi-page, as in Bloomy.
- 2026-11-24: Cookie is `__Host-` prefixed, `SameSite=Lax`, with an Origin check on every state-changing request. Lax because Strict drops the cookie on a link opened from another site (tested in Chromium); sibling subdomains of one registrable domain are same-site either way.
- 2026-11-24: Images, file uploads and Web Push are in version 1; uploads stay on our own disk behind a storage interface and are re-encoded server-side to strip location data.
- 2026-11-24: Target is 1,000 to 5,000 concurrent connections on one node, kept as a learning challenge (real use is under 100 people). No multi-node and no high availability for now. The load test is the last milestone.
- 2026-11-24: Backups are encrypted, cover the database and the uploads together, and are restore-tested by a script.
- 2026-11-24: Shop alerts carry no customer details, and only staff-only "ops" rooms receive them.
- 2026-11-24: This repository is public. No secrets, hostnames or home-lab details in any commit.
- 2026-11-24: MIT licence, the same as Peekbox and Bloomy.
