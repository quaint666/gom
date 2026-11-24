# Gom

A small, invite-only web chat for family and friends, with message history kept on the server so it is there on every device. Web only. The UI is in Vietnamese; code and docs are in English.

Status: usable end to end, deployed to a staging host. Accounts and invites, friends and contacts, one-to-one and group chat (roles, removal, approval, bans) with live delivery, offline sending, replies, forwarding, pins, reactions, editing, taking a message back, delete for me, search, mute and read receipts are built. Not yet: images, files, push notifications, the shop's alert rooms, backups, and the load test.

## Run it locally

Needs the .NET 10 SDK, Node 22 or newer, and Docker.

    docker run -d --name gom-local-pg -p 127.0.0.1:5435:5432 -e POSTGRES_PASSWORD=dev postgres:19
    export ConnectionStrings__Default="Host=127.0.0.1;Port=5435;Username=postgres;Password=dev;Database=postgres"
    (cd src/Gom.Web && npm install && npm run build)   # bundles the TypeScript to wwwroot/js/main.js
    dotnet test
    dotnet run --project src/Gom.Web --urls http://127.0.0.1:5080
    curl -sf http://127.0.0.1:5080/healthz

The first start needs an owner account: set `Owner__GomId` and `Owner__Password` (10 characters or more) once, on an empty database, then remove the password from the configuration. Apply the database migrations first (`dotnet dotnet-ef database update --project src/Gom.Web`); the app does not do it for you.

Decisions and their reasons are in `docs/decisions.md`. The mockups and design notes are in `design/`.
