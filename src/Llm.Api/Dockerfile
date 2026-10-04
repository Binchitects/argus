# The API: sign-in, OIDC, the chat, the admin and every /api route.
# The web (src/web) is its own image; Traefik sends /api, /connect and
# /.well-known here and everything else there.
#
# Built from the repository root: docker build -f src/Llm.Api/Dockerfile .

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

# Arena Code, the coding agent people download from Connect your tools: one
# self-contained file per system (tools/publish-arena-code.sh). ARENA_CODE=auto
# builds it from the runtime packs in tools/offline-nuget when they are there
# and skips it otherwise (the page then says how to add it); online fetches the
# packs from nuget.org; none skips it.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS arena-code
ARG ARENA_CODE=auto
WORKDIR /src
COPY global.json Directory.Build.props VERSION ./
COPY tools/publish-arena-code.sh tools/
COPY tools/offline-nuget/ tools/offline-nuget/
COPY src/ArenaCode/ src/ArenaCode/
RUN mkdir -p dist/arena-code \
 && case "$ARENA_CODE" in \
      none) echo "Arena Code: not built (ARENA_CODE=none)" ;; \
      online) bash tools/publish-arena-code.sh ;; \
      *) if ls tools/offline-nuget/*.nupkg >/dev/null 2>&1; then bash tools/publish-arena-code.sh --offline; \
         else echo "Arena Code: not built, tools/offline-nuget holds no runtime packs (see its README)"; fi ;; \
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
# Arena Code's builds, for download (/api/downloads/arena-code); empty when not built.
COPY --from=arena-code /src/dist/arena-code /downloads/arena-code
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
