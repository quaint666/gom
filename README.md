# Gom

A small, invite-only web chat for family and friends, with message history kept on the server so it is there on every device. Web only. The UI is in Vietnamese; code and docs are in English.

Status: usable end to end, deployed to a staging host. Accounts and invites, friends and contacts, one-to-one and group chat (roles, removal, approval, bans) with live delivery, offline sending, replies, forwarding, pins, reactions, editing, taking a message back, delete for me, search, mute, read receipts, and pictures and files are built. Not yet: push notifications, GIFs and stickers, deleting an account, the shop's alert rooms, backups, and the load test.

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

The first start needs an owner account: set `Owner__GomId` and `Owner__Password` (10 characters or more) once, on an empty database, then remove the password from the configuration. Apply the database migrations first (`dotnet dotnet-ef database update --project src/Gom.Web`); the app does not do it for you.

Decisions and their reasons are in `docs/decisions.md`. The mockups and design notes are in `design/`.

## On a server

The image built from the `Dockerfile` holds the site and the migration bundle (`/app/efbundle`, run before each start). Three things about the surroundings of uploads:

- **A reverse proxy must let a file through.** A file is one request with the file as its body, up to `Uploads__MaxFileBytes` (25 MB by default). A proxy in front has its own limit on a request body, and it answers 413 before the app sees the request when that is smaller. nginx's default is 1 MB (`client_max_body_size`); set it to at least `MaxFileBytes` for the site, and remember that a limit set in a `server` or `location` block overrides one set in `http`.
- **Back up the database and the uploads folder together.** A message row names its files by id, so a file without its row and a row without its file are both useless. Restoring one without the other leaves broken pictures or files nobody can reach.
- **A backup that only ever adds files keeps deleted pictures and files for good** (a recalled message, an ended group). Decide how long deleted files may live in backups, and say so to the people who use Gom.
