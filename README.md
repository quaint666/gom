# Gom

A small web chat for family and friends, joined by invite or by asking and being approved by the owner, with message history kept on the server so it is there on every device. On the web, and as an Android app (`android/`). The UI is in Vietnamese, with English as each person's choice; code and docs are in English.

Status: in use by the family, in production and on a staging host; the Android app is getting ready for Google Play. Built: accounts, invites and requests to join (when the owner opens them; either can add the person to a group), a welcome page for a new account, a setup guide (`/help`), two-step sign-in (each person's choice), friends and blocking, profile pictures, one-to-one and group chat (roles, removal, approval, bans, a group picture) with live delivery, offline sending, replies, forwarding, pins, reactions, editing, taking a message back, delete for me, disappearing messages, search as you type in a conversation, finding friends and groups by name above the list, pictures and files, videos (made smaller on the sending device, up to 45 MB), sound files that play in the message, voice messages, voice calls (on the web and in the app), mute (for a while or until turned back on), a menu on each conversation (pin to the top, mark unread, hide, delete the conversation for yourself), read receipts, notifications (Web Push, and Firebase in the app), emoji, 432 stickers in eleven packs (one of them 256 flags) and the owner's own sticker packs (for everyone, or for chosen people and chats), moving pictures (GIFs), sixteen colours and eight gradients, fifteen backgrounds and pure black to choose from (a conversation may have its own colour and background), deleting your own account, Settings as a menu with an address per part, open pages that move to a new version by themselves (typed words kept across the reload), and for the owner Settings parts for invites, requests, members, sticker packs and contact messages, and a storage page that trims old pictures, files and messages. Not yet: sharing into Gom from other apps, a webhook that posts alerts into a group, link cards, and a load test with uploads (one with 10 million messages of text was run).

## Run it locally

Needs the .NET 10 SDK, Node 22 or newer, and Docker.

    docker run -d --name gom-local-pg -p 127.0.0.1:5435:5432 -e POSTGRES_PASSWORD=dev postgres:19
    export ConnectionStrings__Default="Host=127.0.0.1;Port=5435;Username=postgres;Password=dev;Database=postgres"
    (cd src/Gom.Web && npm install && npm run build)   # bundles the TypeScript to wwwroot/js/main.js and joins Styles/*.css into wwwroot/css/gom.css
    dotnet test
    dotnet run --project src/Gom.Web --urls http://127.0.0.1:5080
    curl -sf http://127.0.0.1:5080/healthz

Uploads need a folder: set `Uploads__Path` to one (without it the upload endpoints answer 503 and the page shows no attach button). On Linux the picture library needs `fontconfig` installed (`apk add fontconfig` on Alpine, `libfontconfig1` on Debian and Ubuntu; Fedora has it).

A JPEG, PNG or WebP is made upright, scaled down (longer side at most 1600 px, plus a 400 px small version) and stored as WebP, so nothing of the original metadata (location, camera, time) is kept. A moving picture (a GIF, or a WebP with frames) is kept as it was sent, after every frame is checked once, with its still first frame as the small version; it plays in the chat with a pause button. An MP4 video (the page converts one before sending, where the device can: 720p, at a bitrate chosen from its length so it fits `MaxVideoBytes`, or 480p for a longer one) is written to disk as it arrives, kept as sent, and played in the chat with a still picture from the sender. Anything else is a file: stored as it is and only ever served as a download, with a warning on app files such as `.apk`. The limits are settings under `Uploads__` and can change without a new build: `MaxImageBytes` (10 MB), `MaxFileBytes` (25 MB), `MaxAnimatedBytes` (8 MB), `MaxVideoBytes` (45 MB), `MaxPosterBytes` (1 MB), `MaxFrames` (1,000), `MaxAnimatedPixels`, `MaxPerMessage` (10), `MaxPixels`, `DailyBytes` (500 MB a person in any 24 hours), the memory and decoding slots, and the pace of uploads per person. An upload that is not put in a message within `PendingHours` (1) is deleted.

Notifications need a key pair: `dotnet run --project src/Gom.Web -- --vapid-keys` prints `Push__PublicKey` and `Push__PrivateKey` (in the image: `dotnet Gom.Web.dll --vapid-keys`); also set `Push__Subject` to a `mailto:` or `https:` address the push services may contact. Without both keys push is off and Settings shows no switch. The private key is a secret, and changing the pair ends every device's notifications (each turns them on again). A device may only register an address at a known push service (Google, including its staging service that Chromium builds use, Mozilla, Apple, Microsoft; `Push__Hosts`), since the server sends to that address. A notification says who wrote and where, and by default the first 100 characters of the message (a sticker, pictures, videos or files: what kind, "Đã gửi 2 ảnh"); each device can choose less in Settings. A message taken back replaces its notification on the devices that showed its words. A browser needs a push service of its own: Chrome, Edge, Firefox and Safari have one, but some Chromium-based browsers removed it (Helium and ungoogled-chromium have none), and there Settings says so instead of a switch that never finishes.

Settings, the privacy page and the welcome page link to where the code and bug reports are: `Site__SourceUrl`, this repository by default; only an https address is shown, and an empty one hides the link. The privacy policy (`/privacy`, which Google Play asks for) names who runs this Gom: set `Site__Operator` to that name. Without it the page says "the person who runs this site". Its contact form keeps each message `Contact__KeepDays` days (180 by default; the page says the number) and takes at most `Contact__DailyMax` messages a day from everyone together (50); the owner reads them in Settings and is told by a notification.

Voice calls (`docs/voice-calls.md`) are off until `Calls__Enabled` is `true`; then `/api/calls` answers and `/api/me` says `calls: true`. A person can be called while one of their pages that takes calls is open (each browser can say it does not, in Settings › Thông báo; a phone's browser does not by default when its person has the app taking calls); `Calls__AppsTakeCalls` (`false`) also counts the Android app, and lets calls wake it, once the app takes calls. `Calls__RingSeconds` (45) is how long a call rings, `Calls__AliveSeconds` (30) how often a phone in a call says it is still there, `Calls__SilentSeconds` (90) how long a silent side is waited for, and `Calls__CallBurst` (3) and `Calls__SecondsBetweenCalls` (20) how fast one person may start calls.

A call's relay is Cloudflare Realtime TURN: set `Turn__KeyId` and `Turn__ApiToken` to a TURN key's ID and secret (both or neither; with only one the app refuses to start). Without them a phone gets a direct connection only, which fails between some networks. `Turn__LoginHours` (3) is how long a relay login lasts before a long call asks for a fresh one.

The Android app is woken through Firebase Cloud Messaging: set `Fcm__ServiceAccount` to a Firebase service account's JSON key file, base64-encoded (`base64 -w0 key.json`); an unreadable value stops the app at start. Without it no app is woken, and nobody can be called (a call needs the person called to have the app signed in).

The first start needs an owner account: set `Owner__GomId` and `Owner__Password` (10 characters or more) once, on an empty database, then remove the password from the configuration. Apply the database migrations first (`dotnet dotnet-ef database update --project src/Gom.Web`); the app does not do it for you.

Sound files play in the message when the server finds them to be sound from their first bytes (MP3, AAC, OGG, WAV, FLAC, or an MP4 with only sound); anything sent before 1.2.0 stays a download. Voice messages are recorded in the browser and sent as WAV (one channel, 16 kHz, about 1.9 MB a minute, at most 5 minutes), so `Uploads__MaxFileBytes` must stay above 10 MB for them. Disappearing messages are deleted by the server every 30 seconds once due, with what they carried. The owner's sticker packs need uploads on (`Uploads__Path`); a picture is at most 1 MB and a pack holds 100.

Two-step sign-in is each person's choice (Settings › Account). A member who loses the phone with their codes gets a password reset link from the owner, which turns it off. The owner who loses theirs runs the app once with `--two-step-off <gomId>` (in the container: `dotnet Gom.Web.dll --two-step-off <gomId>`, with the same configuration); it prints what it did and stops.

Releases (the version number in `VERSION`, how one is made, and what each brought) are in `docs/releases.md`. Decisions and their reasons are in `docs/decisions.md`; what comes next is in `docs/next-session.md`. The mockups and design notes are in `design/`.

The font (Be Vietnam Pro, in `wwwroot/fonts/`) is under the SIL Open Font License 1.1, and the stickers (still images from Google's Noto Emoji, its `2D/svg` folder, in `wwwroot/stickers/`) under the Apache License 2.0, except the flags, which are in the public domain; the licence text sits beside each.

## The Android app

`android/` is the Android app: Gom's pages in a web view, with notifications through Firebase Cloud Messaging and voice calls of its own (`docs/voice-calls.md`); it lets the page use the microphone for voice messages. It needs the Android SDK (`ANDROID_HOME`, or `android/local.properties` with `sdk.dir=…`) and a JDK 17 or newer; the Gradle wrapper fetches the rest.

There are two flavours, `prod` (`io.github.quaint666.gom`) and `staging` (`io.github.quaint666.gom.staging`, installed beside it). Each reads its settings from Gradle properties, normally in `~/.gradle/gradle.properties` and never in this repository:

```
gom.prod.baseUrl=https://…                # the server, https only
gom.prod.alsoAllow=…                      # optional: one more host whose pages may load in the app (a sign-in in front of the server)
gom.prod.firebase.projectId=…             # the Firebase Android app's values; without all four, the build has no notifications
gom.prod.firebase.appId=…
gom.prod.firebase.apiKey=…
gom.prod.firebase.senderId=…
```

and the same with `gom.staging.`. Then `cd android && ./gradlew assembleStagingDebug` builds `app/build/outputs/apk/staging/debug/app-staging-debug.apk`. `./gradlew lintProdDebug testProdDebugUnitTest` is what CI runs (with a placeholder address and no Firebase values).

A release for Google Play is signed with Play's upload key (Google keeps the app's own signing key), from four more Gradle properties on the machine that builds it:

```
gom.upload.storeFile=/…/upload.p12        # the upload keystore
gom.upload.storePassword=…
gom.upload.keyAlias=…
gom.upload.keyPassword=…
```

`./gradlew bundleProdRelease` then builds `app/build/outputs/bundle/prodRelease/app-prod-release.aab`; without all four, a release build stops at once. Its version name is `VERSION`, and its version code the same number written as one (1.2.3 is 10203).

## On a server

The image built from the `Dockerfile` holds the site and the migration bundle (`/app/efbundle`, run before each start). Seven things about its surroundings:

- **Say how many proxies stand in front.** `ForwardedHeaders__Hops` is the number of proxies (for example 2 for two proxies in a row); the visitor's address, which the sign-in limits key on, is picked out of `X-Forwarded-For` by that count. It is 0 by default, so a header a visitor sends is never believed, and with it set the app must be reachable only through those proxies.
- **A reverse proxy must let a file through.** A file is one request with the file as its body, up to `Uploads__MaxVideoBytes` for a video (45 MB by default) and `Uploads__MaxFileBytes` for anything else (25 MB). A proxy in front has its own limit on a request body, and it answers 413 before the app sees the request when that is smaller. nginx's default is 1 MB (`client_max_body_size`); set it to at least the larger of the two for the upload address (`/api/conversations/{id}/attachments`; a video's still picture, at most `MaxPosterBytes`, goes to `/api/attachments/{id}/poster`), and remember that a limit set in a `server` or `location` block overrides one set in `http`.
- **Notifications go out to the push services** (`fcm.googleapis.com`, `jmt17.google.com` (Chromium builds and pre-release Chrome), `*.push.services.mozilla.com`, `*.push.apple.com`, `*.notify.windows.com`) over HTTPS: the server must be able to reach them on port 443.
- **A deploy reaches open pages by itself.** Each page learns the new version when its socket reconnects (or within ten minutes) and reloads when that loses nothing. A change that pages loaded before it cannot work with (an API changed under them) raises `Pages.PageVersion`; older pages are then refused with "update_required" and must reload.
- **Gom keeps at most 30 database connections** unless the connection string names its own (`Maximum Pool Size=…`); PostgreSQL's default allows 100. PostgreSQL needs about 260 MB under heavy use: in a test with 10 million messages it was killed for memory at 256 MB and ran clean at 512 MB. The migration `SearchIndex` builds an index over every message once: seconds at a family's size, but a database of millions of messages needs `Command Timeout=600` on the migration bundle's connection string for that run.
- **Back up the database and the uploads folder together.** A message row names its files by id, so a file without its row and a row without its file are both useless. Restoring one without the other leaves broken pictures or files nobody can reach.
- **Copy the uploads folder as a mirror, so deletions reach the backup.** Gom deletes files when a message is taken back or disappears, a group ends, an account is deleted, a profile picture changes, an upload is never sent, and when the owner trims. The owner's stickers are in the same folder, so a backup needs them too. A copy that only ever adds files keeps all of these for good, and the owner's promise that a trim is gone for good would not hold. Guard the mirror so a missing or empty source folder is never copied over the backup. A trimmed picture or file keeps its row with `removed_at` set and has no file on purpose: a restore check must skip those rows. Database dumps and whole-machine snapshots keep deleted data until they age out; say how long to the people who use Gom.
