# syntax=docker/dockerfile:1

# Argus: the .NET backend serving the React app, the code index, knowledge
# packs, MCP and the chat API, on one port.
#
#   docker build --target server -t argus .
#
# The image exists only if the xUnit suite passed inside it (the test stage's
# receipt is copied into the runtime image, which keeps that stage in the
# build graph). The browser suite (frontend/e2e) needs Chromium and runs in CI.
#
# Ubuntu 24.04 (noble) bases: its universal-ctags decides which symbols exist,
# so the base is pinned and the test stage turns any drift into a failed build.

ARG DOTNET_TAG=10.0-noble
ARG NODE_TAG=22-bookworm-slim

# ------------------------------------------------------------- frontend -----
FROM node:${NODE_TAG} AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npm run build

# ---------------------------------------------------------------- build -----
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG} AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends git universal-ctags ca-certificates curl unzip \
 && rm -rf /var/lib/apt/lists/* \
 && ctags --version | grep -q "Universal Ctags"
WORKDIR /src
COPY dotnet/global.json dotnet/Directory.Build.props dotnet/Argus.sln dotnet/
COPY dotnet/src/Argus/Argus.csproj dotnet/src/Argus/
COPY dotnet/tests/Argus.Tests/Argus.Tests.csproj dotnet/tests/Argus.Tests/
RUN dotnet restore dotnet/Argus.sln
COPY dotnet/ dotnet/
# The sqlite-vec release, from its PyPI wheel, checked against a pinned SHA-256.
RUN case "$(dpkg --print-architecture)" in \
      amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) echo "unsupported arch" >&2; exit 1 ;; \
    esac \
 && dotnet/scripts/fetch-sqlite-vec.sh "$rid"

# ----------------------------------------------------------------- test -----
FROM build AS test
RUN git config --global user.email build@argus.invalid && git config --global user.name build \
 && dotnet test dotnet/Argus.sln -c Release --no-restore \
 && { echo "suite: passed"; \
      echo "ctags: $(ctags --version | head -1)"; \
      echo "dotnet: $(dotnet --version)"; \
      echo "built: $(date -u +%Y-%m-%dT%H:%M:%SZ)"; } > /src/.build-verified

FROM build AS publish
# Framework-dependent and RID-specific: only this platform's native SQLite.
RUN case "$(dpkg --print-architecture)" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; esac \
 && dotnet publish dotnet/src/Argus/Argus.csproj -c Release -r "$rid" --self-contained false -o /out \
 && rm -f /out/*.pdb

# -------------------------------------------------------------- runtime -----
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_TAG} AS runtime

ARG ARGUS_VERSION=3.0.0
ARG ARGUS_REVISION=unknown
LABEL org.opencontainers.image.title="argus" \
      org.opencontainers.image.description="Chat, code index and documentation for a team, on local models" \
      org.opencontainers.image.version="${ARGUS_VERSION}" \
      org.opencontainers.image.revision="${ARGUS_REVISION}" \
      org.opencontainers.image.licenses="GPL-3.0-only"
ENV ARGUS_VERSION="${ARGUS_VERSION}" \
    ARGUS_REVISION="${ARGUS_REVISION}" \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1

RUN apt-get update \
 && apt-get install -y --no-install-recommends git universal-ctags ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && ctags --version | grep -q "Universal Ctags"

# UID/GID 10001, so an existing argus-data volume is readable without a chown.
ARG ARGUS_UID=10001
ARG ARGUS_GID=10001
RUN groupadd --gid "${ARGUS_GID}" argus \
 && useradd --uid "${ARGUS_UID}" --gid "${ARGUS_GID}" --create-home --shell /usr/sbin/nologin argus

COPY --from=publish /out /app
COPY --from=frontend /src/frontend/dist /app/wwwroot
RUN ln -s /app/argus /usr/local/bin/argus
COPY --from=test /src/.build-verified /usr/share/argus/build-verified

RUN mkdir -p /var/lib/argus \
 && chown -R argus:argus /var/lib/argus \
 && git config --system --add safe.directory '*'

USER argus
VOLUME ["/var/lib/argus"]
ENTRYPOINT ["argus"]
CMD ["status", "--config", "/etc/argus/config.yaml"]

# --------------------------------------------------------------- server -----
FROM runtime AS server
EXPOSE 7700
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 CMD ["argus", "healthcheck"]
CMD ["serve", "--config", "/etc/argus/config.yaml", "--host", "0.0.0.0", "--port", "7700"]
