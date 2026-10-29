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
# The skills that come with Arena, built into the app (and Code Arena, below).
COPY skills/ skills/
# The compliance controls, which Admin → Compliance shows.
COPY docs/compliance/controls.yaml docs/compliance/frameworks.yaml docs/compliance/
COPY docs/compliance/catalogs/ docs/compliance/catalogs/
COPY docs/compliance/policies/ docs/compliance/policies/
COPY docs/compliance/isms.md docs/compliance/
COPY deploy/features deploy/
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
# in tools/offline-nuget (tools/fetch-code-arena-packs.sh keeps them at the
# version this SDK publishes with, and the deploy runs it); when they are not
# there at that version, from nuget.org's, and only when neither is to be had
# is it skipped (the page then says how to add it): a newer sdk:10.0 than the
# packs is no failed image, nor one without Code Arena.
# online fetches the packs from nuget.org; none skips it.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS code-arena
ARG CODE_ARENA
WORKDIR /src
COPY global.json Directory.Build.props VERSION ./
COPY tools/publish-code-arena.sh tools/
COPY tools/offline-nuget/ tools/offline-nuget/
COPY src/CodeArena/ src/CodeArena/
COPY skills/ skills/
# The files Code Arena shares with the app (linked in its project): how a context refusal reads, and how earlier messages are found by their words.
COPY src/Llm.Core/Chat/ContextOverflow.cs src/Llm.Core/Chat/HistoryWords.cs src/Llm.Core/Chat/
COPY src/Argus/Util/CodeText.cs src/Argus/Util/
# The arena module a run_code program imports (code mode), the sandbox's own, embedded.
COPY deploy/services/sandbox/arena/__init__.py deploy/services/sandbox/arena/
COPY --from=code-arena-web /CodeArena/web/ src/CodeArena/web/
RUN mkdir -p dist/code-arena \
 && case "$CODE_ARENA" in \
      none) echo "Code Arena: not built (CODE_ARENA=none)" ;; \
      online) bash tools/publish-code-arena.sh ;; \
      *) bash tools/publish-code-arena.sh --offline \
         || { test $? -eq 3 && echo "Code Arena: publishing with nuget.org's packs instead" \
              && { bash tools/publish-code-arena.sh \
                   || echo "Code Arena: not built, neither tools/offline-nuget nor nuget.org had the packs (tools/fetch-code-arena-packs.sh, see tools/offline-nuget/README.md)"; }; } ;; \
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
