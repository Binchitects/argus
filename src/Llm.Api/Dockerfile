# The API: sign-in, OIDC, the chat, the admin and every /api route.
# The web (src/web) is its own image; Traefik sends /api, /connect and
# /.well-known here and everything else there.
#
# Built from the repository root: docker build -f src/Llm.Api/Dockerfile .

# Code Arena's builds (below): auto, online or none.
ARG CODE_ARENA=auto

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
# Empty: VERSION at the repository's root (Directory.Build.props). Passed on only when
# set: an empty -p:AppVersion= is a global property and would win over VERSION.
ARG APP_VERSION=
WORKDIR /src
COPY global.json Directory.Build.props VERSION ./
COPY src/Llm.Core/Llm.Core.csproj src/Llm.Core/
COPY src/Llm.Api/Llm.Api.csproj src/Llm.Api/
RUN dotnet restore src/Llm.Api/Llm.Api.csproj
COPY src/Llm.Core/ src/Llm.Core/
COPY src/Llm.Api/ src/Llm.Api/
RUN dotnet publish src/Llm.Api/Llm.Api.csproj -c Release --no-restore -o /out ${APP_VERSION:+-p:AppVersion=$APP_VERSION} \
 && rm -f /out/*.pdb \
 && mkdir -p /shared/engine-config /shared/directory /shared/sandbox

# Code Arena's web interface (code-arena web): its page, built from src/web
# into /CodeArena/web. The first steps are src/web's own image's, to the
# letter (the same base, and npm ci over the same lock file): a host that
# built the web image has that layer cached, and downloads nothing here.
FROM node:22-alpine AS code-arena-web-build
WORKDIR /web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/web/ ./
RUN node_modules/.bin/vite build --config vite.code-arena.config.ts \
 && node_modules/.bin/vite build --config vite.preview.config.ts --outDir ../CodeArena/web
# CODE_ARENA=none builds no Code Arena, so no page either (and no npm at all).
FROM node:22-alpine AS code-arena-web-none
RUN mkdir -p /CodeArena/web
FROM code-arena-web-build AS code-arena-web-auto
FROM code-arena-web-build AS code-arena-web-online
FROM code-arena-web-${CODE_ARENA} AS code-arena-web

# Code Arena, the coding agent people download from Connect your tools: one
# self-contained file per system (tools/publish-code-arena.sh), its web
# interface's page embedded. CODE_ARENA=auto builds it from the runtime packs
# in tools/offline-nuget when they are there and skips it otherwise (the page
# then says how to add it); online fetches the packs from nuget.org; none skips it.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS code-arena
ARG CODE_ARENA
WORKDIR /src
COPY global.json Directory.Build.props VERSION ./
COPY tools/publish-code-arena.sh tools/
COPY tools/offline-nuget/ tools/offline-nuget/
COPY src/CodeArena/ src/CodeArena/
COPY --from=code-arena-web /CodeArena/web/ src/CodeArena/web/
RUN mkdir -p dist/code-arena \
 && case "$CODE_ARENA" in \
      none) echo "Code Arena: not built (CODE_ARENA=none)" ;; \
      online) bash tools/publish-code-arena.sh ;; \
      *) if ls tools/offline-nuget/*.nupkg >/dev/null 2>&1; then bash tools/publish-code-arena.sh --offline; \
         else echo "Code Arena: not built, tools/offline-nuget holds no runtime packs (see its README)"; fi ;; \
    esac

# Chiseled: no shell, no package manager, runs as a non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=api /out ./
# Time zones for the chat's date and time tool: the chiseled image has none,
# and the -extra image carries ICU as well, which nothing here needs.
COPY --from=api /usr/share/zoneinfo /usr/share/zoneinfo
# The dashboards, in Grafana's JSON: the app runs every panel's query itself.
COPY src/Llm.Api/Dashboards/json /dashboards
# The plugins that come with the app (Admin → Plugins).
COPY plugins /plugins
# Code Arena's builds, for download (/api/downloads/code-arena); empty when not built.
COPY --from=code-arena /src/dist/code-arena /downloads/code-arena
# What it shares with other services, as named volumes: a new volume starts as these, the app's.
COPY --from=api --chown=1000:1000 /shared/ /
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Database__Host=postgres \
    Database__Username=arena \
    Auth__DirectoryFile=/directory/users.yml
# The owner of MODELS_DIR on most hosts: downloads land there as theirs.
USER 1000:1000
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 CMD ["dotnet", "Llm.Api.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Llm.Api.dll"]
