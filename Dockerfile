# Gom: one image from this file.
#
#   docker build -t gom .                                           the site, and the migration bundle at /app/efbundle
#   docker run --entrypoint /app/efbundle -e ConnectionStrings__Default=... gom
#                                                                   creates and updates the tables (run it before the site
#                                                                   starts; a second run changes nothing)
#
# Stage "assets" bundles the browser TypeScript (tsc is checked by the tests, esbuild only bundles) and makes the fingerprinted copies
# of the files a page loads, with their list (build.mjs; it needs the stylesheet and theme-init.js to hash them). Stage "publish" publishes
# the site; stage "bundle" builds the migration bundle with the dotnet-ef version pinned in dotnet-tools.json. They share the
# restored packages (stage "restore"). The bundle stage copies only the C# folders, so an edit to a page, a style or a script
# does not rebuild it. Only the output of "publish" and "bundle" ships: no SDK, no Node and no source in the final image.
#
# Every base image is pinned by tag and digest (the same as Peekbox's, checked on 2026-11-20). Bump them on purpose, together:
# the runtime must not be older than the EF packages in the csproj (10.0.15).
FROM node:26.13.1-alpine3.24@sha256:6bd9ab43c0ec0190ca7b2d193906cb8b7edf6e7f4afa43ddeccd6452710bffb5 AS assets

WORKDIR /build
COPY src/Gom.Web/package.json src/Gom.Web/package-lock.json ./
RUN npm ci --ignore-scripts
COPY src/Gom.Web/build.mjs ./
COPY src/Gom.Web/Scripts ./Scripts
COPY src/Gom.Web/wwwroot/css ./wwwroot/css
COPY src/Gom.Web/wwwroot/theme-init.js ./wwwroot/
# The check fails the build if nothing came out.
RUN npm run build && [ -s wwwroot/js/main.js ] && [ -s wwwroot/js/video.js ] && [ -s wwwroot/assets.json ]

FROM mcr.microsoft.com/dotnet/sdk:10.0.404-alpine3.24@sha256:0294f285d062afacc8b5f6d7febb0cd4ee879320e5585fa15f2f4ebf1feb9ba7 AS restore

WORKDIR /src
# Restore first: this layer is reused until a package changes.
COPY Directory.Build.props VERSION dotnet-tools.json ./
COPY src/Gom.Web/Gom.Web.csproj src/Gom.Web/
# The runtime is named, so only the native files for this one (the picture library has them for every system) are restored and shipped.
RUN dotnet restore src/Gom.Web/Gom.Web.csproj -r linux-musl-x64 && dotnet tool restore

FROM restore AS publish
COPY src/Gom.Web ./src/Gom.Web
# The bundle, the fingerprinted copies and their list land beside the files they were made from (the same ones, copied over themselves).
COPY --from=assets /build/wwwroot ./src/Gom.Web/wwwroot
RUN dotnet publish src/Gom.Web/Gom.Web.csproj -c Release --no-restore -r linux-musl-x64 --self-contained false -o /out/web

FROM restore AS bundle
# Only what the bundle compiles. A NEW folder of C# in src/Gom.Web goes in this list too (a missing one fails the build with a
# compile error, never silently). Scripts and wwwroot are left out on purpose, so this stage stays cached when only they change.
COPY src/Gom.Web/Program.cs src/Gom.Web/Assets.cs src/Gom.Web/Pages.cs src/Gom.Web/ServerText.cs src/Gom.Web/appsettings.json src/Gom.Web/
COPY src/Gom.Web/Auth ./src/Gom.Web/Auth
COPY src/Gom.Web/Chat ./src/Gom.Web/Chat
COPY src/Gom.Web/Data ./src/Gom.Web/Data
COPY src/Gom.Web/Migrations ./src/Gom.Web/Migrations
COPY src/Gom.Web/Push ./src/Gom.Web/Push
COPY src/Gom.Web/Social ./src/Gom.Web/Social
COPY src/Gom.Web/Uploads ./src/Gom.Web/Uploads
# The app stops at start without a connection string, so finding the DbContext here needs a placeholder; the real one is
# given when the bundle runs. The bundle is not self-contained: it runs on the .NET runtime the site image already has.
RUN ConnectionStrings__Default="Host=placeholder;Database=placeholder" \
    dotnet ef migrations bundle --project src/Gom.Web -r linux-musl-x64 -o /out/efbundle \
 && [ -x /out/efbundle ]

# The one image: the site, and the migration bundle at /app/efbundle (run it with `--entrypoint /app/efbundle`). Port 8080 and
# the unprivileged "app" user come from the base image. The files stay root-owned: the site never writes to its own folder.
# Alpine's dotnet images turn globalization off, so the packages are added and the switch is turned off (names and text are
# Vietnamese). krb5-libs: without it the database driver prints "Cannot load library libgssapi_krb5.so.2" when it connects.
# fontconfig: the picture library's native part (SkiaSharp, for making uploaded pictures smaller) is linked against it, even though
# no text is ever drawn; without it the first upload fails with "Unable to load shared library 'libSkiaSharp'".
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-alpine3.24@sha256:21c3f9798416cba4c0576a947c286231cb71ea2884896cb968253f5c9668f930 AS web
RUN apk add --no-cache icu-libs icu-data-full tzdata krb5-libs fontconfig
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /app
COPY --from=publish /out/web ./
COPY --from=bundle /out/efbundle /app/efbundle
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Gom.Web.dll"]
