# code-intel: the .NET code index service (index, knowledge packs, MCP, its admin
# API) with its own React app (src/code-intel-web), on one port.
#
# Built from the repository root:
#   docker build -f src/CodeIntel/Dockerfile --target server -t code-intel .
#
# The image exists only if the xUnit suite passed inside it (the test stage's
# receipt is copied into the runtime image, which keeps that stage in the
# build graph). The browser suite (src/code-intel-web/e2e) needs Chromium and runs in CI.
#
# Ubuntu 24.04 (noble) bases: its universal-ctags decides which symbols exist,
# so the base is pinned and the test stage turns any drift into a failed build.

ARG DOTNET_TAG=10.0-noble
# The web builds on the same Node image as the platform's (src/web).
ARG NODE_TAG=22-alpine

# ------------------------------------------------------------- frontend -----
FROM node:${NODE_TAG} AS frontend
WORKDIR /src/code-intel-web
COPY src/code-intel-web/package.json src/code-intel-web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/code-intel-web/ ./
# The service's name people see: ../../branding.env from here, unless CODE_INTEL_NAME is given.
COPY branding.env /branding.env
ARG CODE_INTEL_NAME=
ENV CODE_INTEL_NAME=$CODE_INTEL_NAME
RUN npm run build

# ---------------------------------------------------------------- build -----
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG} AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends git universal-ctags ca-certificates curl unzip \
 && rm -rf /var/lib/apt/lists/* \
 && ctags --version | grep -q "Universal Ctags"
WORKDIR /src
COPY tools/fetch-sqlite-vec.sh tools/
# The sqlite-vec release, from its PyPI wheel, checked against a pinned SHA-256: before
# VERSION and the sources, so a release does not fetch it again. arenactl build fetches it on the
# host first (native/runtimes): the script finds it there and reads nothing from PyPI.
COPY native/runtimes/ native/runtimes/
RUN case "$(dpkg --print-architecture)" in \
      amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) echo "unsupported arch" >&2; exit 1 ;; \
    esac \
 && tools/fetch-sqlite-vec.sh "$rid"
COPY global.json Directory.Build.props VERSION branding.env ./
COPY src/Shared/ src/Shared/
# The names people see (branding.env's unless given): PRODUCT_NAME and CODE_INTEL_NAME, read by Directory.Build.props.
ARG PRODUCT_NAME=
ARG CODE_INTEL_NAME=
ENV PRODUCT_NAME=$PRODUCT_NAME CODE_INTEL_NAME=$CODE_INTEL_NAME
COPY src/CodeIntel/CodeIntel.csproj src/CodeIntel/
COPY tests/CodeIntel.Tests/CodeIntel.Tests.csproj tests/CodeIntel.Tests/
RUN dotnet restore tests/CodeIntel.Tests/CodeIntel.Tests.csproj
COPY src/CodeIntel/ src/CodeIntel/
# Code Arena's scanners and code graph, linked by CodeIntel.csproj (the symbol graph): these files, not Code Arena.
COPY src/CodeArena/CodeScan.cs src/CodeArena/CodeGraph.cs src/CodeArena/CodeIndex.cs src/CodeArena/CodeFiles.cs src/CodeArena/
COPY tests/CodeIntel.Tests/ tests/CodeIntel.Tests/
COPY tests/fixtures/code-intel/ tests/fixtures/code-intel/

# ----------------------------------------------------------------- test -----
FROM build AS test
RUN git config --global user.email build@code-intel.invalid && git config --global user.name build \
 && dotnet test tests/CodeIntel.Tests/CodeIntel.Tests.csproj -c Release --no-restore \
 && { echo "suite: passed"; \
      echo "ctags: $(ctags --version | head -1)"; \
      echo "dotnet: $(dotnet --version)"; \
      echo "built: $(date -u +%Y-%m-%dT%H:%M:%SZ)"; } > /src/.build-verified

FROM build AS publish
# Framework-dependent and RID-specific: only this platform's native SQLite.
RUN case "$(dpkg --print-architecture)" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; esac \
 && dotnet publish src/CodeIntel/CodeIntel.csproj -c Release -r "$rid" --self-contained false -o /out \
 && rm -f /out/*.pdb

# -------------------------------------------------------------- runtime -----
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_TAG} AS runtime

# Empty: the assembly's, from VERSION at the repository's root.
ARG CODE_INTEL_VERSION=
ARG CODE_INTEL_REVISION=unknown
LABEL org.opencontainers.image.title="code-intel" \
      org.opencontainers.image.description="Chat, code index and documentation for a team, on local models" \
      org.opencontainers.image.version="${CODE_INTEL_VERSION}" \
      org.opencontainers.image.revision="${CODE_INTEL_REVISION}" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.source="https://github.com/Binchitects/argus"
ENV CODE_INTEL_VERSION="${CODE_INTEL_VERSION}" \
    CODE_INTEL_REVISION="${CODE_INTEL_REVISION}" \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1

RUN apt-get update \
 && apt-get install -y --no-install-recommends git universal-ctags ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && ctags --version | grep -q "Universal Ctags"

# UID/GID 10001, so an existing code-intel-data volume is readable without a chown.
ARG CODE_INTEL_UID=10001
ARG CODE_INTEL_GID=10001
RUN groupadd --gid "${CODE_INTEL_GID}" code-intel \
 && useradd --uid "${CODE_INTEL_UID}" --gid "${CODE_INTEL_GID}" --create-home --shell /usr/sbin/nologin code-intel

COPY --from=publish /out /app
COPY --from=frontend /src/code-intel-web/dist /app/wwwroot
RUN ln -s /app/code-intel /usr/local/bin/code-intel
COPY --from=test /src/.build-verified /usr/share/code-intel/build-verified

RUN mkdir -p /var/lib/code-intel \
 && chown -R code-intel:code-intel /var/lib/code-intel \
 && git config --system --add safe.directory '*'

USER code-intel
VOLUME ["/var/lib/code-intel"]
ENTRYPOINT ["code-intel"]
CMD ["status", "--config", "/etc/code-intel/config.yaml"]

# --------------------------------------------------------------- server -----
FROM runtime AS server
EXPOSE 7700
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 CMD ["code-intel", "healthcheck"]
CMD ["serve", "--config", "/etc/code-intel/config.yaml", "--host", "0.0.0.0", "--port", "7700"]
