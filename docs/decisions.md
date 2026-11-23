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
- 2026-11-24: Friends are added by exact Gom ID, a link or a QR code, as a request the other person accepts; signing up through an invite makes you friends with the inviter. No phone-number lookup in version 1: it needs verified numbers (an SMS provider), stores personal data, and lets people find out who is on Gom.
- 2026-11-24: Only the owner invites, with a single-use link that expires; an invite can also put the new member in a group. Invite-only stays true because nobody else can open the door.
- 2026-11-24: Any member can create a group and add their own friends; members who are not friends need a friend request before a one-to-one message. Group links, avatar images and profile customization come after version 1 (a group link would let people in without an invite).
- 2026-11-24: Read receipts are in version 1 as one read position per member per conversation (`last_read_seq`), with an opt-out that hides yours and theirs; typing indicators and online status are not in version 1 (they would live in memory only). A position is one write however many messages it covers, and the other two need no storage.
- 2026-11-24: Ops rooms show in a staff member's list, are muted for push by default and are read-only (the shop posts alerts as a bot). Staff talk elsewhere; one rule for the room is simpler than two.
- 2026-11-24: A message sent while the connection is down waits and goes out when it returns; a failed send is retried with the same `client_id`, so a retry cannot duplicate it.
- 2026-11-24: Recall ("thu hồi") replaces a message with a placeholder for everyone, with no time limit in version 1.
- 2026-11-24: The look is Peekbox's sage and cream in Be Vietnam Pro, with avatars and sender names neutral so the colour stays one hue; light and dark follow the system and can be toggled. Mockups, tokens and notes are in `design/`.
- 2026-11-24: Sign-in is Gom ID plus password in version 1; passkeys may come later as an extra method. Relatives on old phones need something that works everywhere. .NET's built-in passkeys are scoped to full ASP.NET Core Identity, so adding them later means Identity or a WebAuthn library.
- 2026-11-24: Our own `users` table with `PasswordHasher<T>` (PBKDF2, iteration count set to 600,000 following OWASP; the .NET default is 100,000), not full Identity. We do not use its roles, email columns or UI.
- 2026-11-24: Sessions are server-side: the cookie holds a random token and the database stores only its hash. This allows sign-out per device, revocation, and a re-check on the WebSocket upgrade. A person can see and end their own other sessions.
- 2026-11-24: Only the owner resets a password, with a single-use link built like an invite. There is no email or phone in version 1, so nothing else could prove who is asking.
- 2026-11-24: Passwords are at least 10 characters with no composition rules, spaces allowed, at most about 128, and the most common ones are rejected. Sign-in attempts are slowed per Gom ID and per IP, never hard-locked, and a missing ID is hashed anyway so timing does not reveal it.
- 2026-11-24: A Gom ID is lowercase letters, digits and underscore, 3 to 20 characters, unique ignoring case, with reserved words blocked, and cannot be changed in version 1. A separate display name (a nickname, as in Zalo) defaults to the Gom ID.
- 2026-11-24: An invite lasts 7 days, is single-use, and the owner can revoke it at any time. Invites travel through other chat apps, so 48 hours would be too short.
- 2026-11-24: The owner account is created at first start from `Owner__GomId` and `Owner__Password` when the users table is empty, then the password is removed from the configuration. No web page and no command, so nobody can race the owner to it. Losing the owner password later means a database edit by the host's operator.
- 2026-11-24: Signing up through an invite signs the new member in. A reset link lasts 24 hours, only one is live per person, and using it ends all that person's other sessions and signs them in.
- 2026-11-24: The browser code is bundled with esbuild into one `wwwroot/js/main.js`, type-checked with `tsc` and tested with vitest and jsdom (Peekbox's checks). Plain `tsc` output cannot resolve `import 'lit-html'` in a browser without an import map or a copy out of `node_modules`; one bundle for all pages is the smallest fix.
- 2026-11-24: Every response carries a strict Content-Security-Policy (own script and styles only, no inline, never framed); pages and API answers are never cached, and no page sends a Referer, because an invite or reset link carries its token in the address. So pages hold no inline script or style; the colour scheme is set by a blocking `theme-init.js` before the styles load.
