# Gom

A small, invite-only web chat for family and friends, with message history kept on the server so it is there on every device. Web only. The UI is in Vietnamese; code and docs are in English.

Status: scaffolding (M0). Nothing to use yet.

## Run it locally

Needs the .NET 10 SDK and Docker.

    docker run -d --name gom-local-pg -p 127.0.0.1:5434:5432 -e POSTGRES_PASSWORD=dev postgres:19
    export ConnectionStrings__Default="Host=127.0.0.1;Port=5434;Username=postgres;Password=dev;Database=postgres"
    dotnet test
    dotnet run --project src/Gom.Web --urls http://127.0.0.1:5080
    curl -sf http://127.0.0.1:5080/healthz

Decisions and their reasons are in `docs/decisions.md`.
