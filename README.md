# Gom

A small web chat for family and friends, joined by invite or by asking and being approved by the owner, with message history kept on the server so it is there on every device. Web only. The UI is in Vietnamese; code and docs are in English.

Status: in use by the family, in production and on a staging host. Built: accounts, invites and requests to join (when the owner opens them), friends and blocking, profile pictures, one-to-one and group chat (roles, removal, approval, bans) with live delivery, offline sending, replies, forwarding, pins, reactions, editing, taking a message back, delete for me, search, mute (for a while or until turned back on), a menu on each conversation (pin to the top, mark unread, hide, delete the conversation for yourself), read receipts, pictures and files, notifications (Web Push), emoji and 432 stickers in eleven packs (one of them 256 flags), deleting your own account, and for the owner a storage page that trims old pictures, files and messages. Not yet: sharing into Gom from other apps, a webhook that posts alerts into a group, link cards, two-step sign-in for the owner, and the load test. GIFs were dropped.

## Run it locally

Needs the .NET 10 SDK, Node 22 or newer, and Docker.

    docker run -d --name gom-local-pg -p 127.0.0.1:5435:5432 -e POSTGRES_PASSWORD=dev postgres:19
    export ConnectionStrings__Default="Host=127.0.0.1;Port=5435;Username=postgres;Password=dev;Database=postgres"
    (cd src/Gom.Web && npm install && npm run build)   # bundles the TypeScript to wwwroot/js/main.js
    dotnet test
    dotnet run --project src/Gom.Web --urls http://127.0.0.1:5080
    curl -sf http://127.0.0.1:5080/healthz

Uploads need a folder: set `Uploads__Path` to one (without it the upload endpoints answer 503 and the page shows no attach button). On Linux the picture library needs `fontconfig` installed (`apk add fontconfig` on Alpine, `libfontconfig1` on Debian and Ubuntu; Fedora has it).

A JPEG, PNG or WebP is made upright, scaled down (longer side at most 1600 px, plus a 400 px small version) and stored as WebP, so nothing of the original metadata (location, camera, time) is kept. Anything else, a GIF included, is a file: stored as it is and only ever served as a download. The limits are settings under `Uploads__` and can change without a new build: `MaxImageBytes` (10 MB), `MaxFileBytes` (25 MB), `MaxPerMessage` (10), `MaxPixels`, the memory and decoding slots, and the pace of uploads per person. An upload that is not put in a message within `PendingHours` (1) is deleted.

Notifications need a key pair: `dotnet run --project src/Gom.Web -- --vapid-keys` prints `Push__PublicKey` and `Push__PrivateKey` (in the image: `dotnet Gom.Web.dll --vapid-keys`); also set `Push__Subject` to a `mailto:` or `https:` address the push services may contact. Without both keys push is off and Settings shows no switch. The private key is a secret, and changing the pair ends every device's notifications (each turns them on again). A device may only register an address at a known push service (Google, including its staging service that Chromium builds use, Mozilla, Apple, Microsoft; `Push__Hosts`), since the server sends to that address. A notification says who wrote and where, and by default the first 100 characters of the message (a sticker, pictures or files: what kind, "Đã gửi 2 ảnh"); each device can choose less in Settings. A message taken back replaces its notification on the devices that showed its words. A browser needs a push service of its own: Chrome, Edge, Firefox and Safari have one, but some Chromium-based browsers removed it (Helium and ungoogled-chromium have none), and there Settings says so instead of a switch that never finishes.

The first start needs an owner account: set `Owner__GomId` and `Owner__Password` (10 characters or more) once, on an empty database, then remove the password from the configuration. Apply the database migrations first (`dotnet dotnet-ef database update --project src/Gom.Web`); the app does not do it for you.

Decisions and their reasons are in `docs/decisions.md`; what comes next is in `docs/next-session.md`. The mockups and design notes are in `design/`.

The font (Be Vietnam Pro, in `wwwroot/fonts/`) is under the SIL Open Font License 1.1, and the stickers (still images from Google's Noto Emoji, its `2D/svg` folder, in `wwwroot/stickers/`) under the Apache License 2.0, except the flags, which are in the public domain; the licence text sits beside each.

## On a server

The image built from the `Dockerfile` holds the site and the migration bundle (`/app/efbundle`, run before each start). Five things about its surroundings:

- **Say how many proxies stand in front.** `ForwardedHeaders__Hops` is the number of proxies (for example 2 for two proxies in a row); the visitor's address, which the sign-in limits key on, is picked out of `X-Forwarded-For` by that count. It is 0 by default, so a header a visitor sends is never believed, and with it set the app must be reachable only through those proxies.
- **A reverse proxy must let a file through.** A file is one request with the file as its body, up to `Uploads__MaxFileBytes` (25 MB by default). A proxy in front has its own limit on a request body, and it answers 413 before the app sees the request when that is smaller. nginx's default is 1 MB (`client_max_body_size`); set it to at least `MaxFileBytes` for the site, and remember that a limit set in a `server` or `location` block overrides one set in `http`.
- **Notifications go out to the push services** (`fcm.googleapis.com`, `jmt17.google.com` (Chromium builds and pre-release Chrome), `*.push.services.mozilla.com`, `*.push.apple.com`, `*.notify.windows.com`) over HTTPS: the server must be able to reach them on port 443.
- **Back up the database and the uploads folder together.** A message row names its files by id, so a file without its row and a row without its file are both useless. Restoring one without the other leaves broken pictures or files nobody can reach.
- **Copy the uploads folder as a mirror, so deletions reach the backup.** Gom deletes files when a message is taken back, a group ends, an account is deleted, a profile picture changes, an upload is never sent, and when the owner trims. A copy that only ever adds files keeps all of these for good, and the owner's promise that a trim is gone for good would not hold. Guard the mirror so a missing or empty source folder is never copied over the backup. A trimmed picture or file keeps its row with `removed_at` set and has no file on purpose: a restore check must skip those rows. Database dumps and whole-machine snapshots keep deleted data until they age out; say how long to the people who use Gom.
