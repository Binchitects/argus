# syntax=docker/dockerfile:1

# Argus, .NET implementation. A drop-in for the Python image built from the
# repository-root Dockerfile: same CLI, same MCP surface, same database, same
# /var/lib/argus volume and the same UID, so switching an existing deployment
# is a rebuild, not a migration.
#
#   docker build -f dotnet/Dockerfile --target server -t argus-dotnet .
#
# The build context is the REPOSITORY ROOT, not dotnet/: the index schema is
# the Python migrations (embedded, never copied), and the test stage reuses
# the Python suite's fixtures.
#
# Base images are Ubuntu 24.04 (noble). Its universal-ctags is the 5.9 build
# the conformance run (dotnet/tests/conformance) compared against the Python
# image's, and ctags output is what decides which symbols exist -- so the
# base is pinned for the same reason the Python image pins bookworm, and the
# test stage below turns any drift into a failed build.

ARG DOTNET_TAG=10.0-noble

# ---------------------------------------------------------------- build -----
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG} AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends git universal-ctags ca-certificates curl unzip \
 && rm -rf /var/lib/apt/lists/* \
 && ctags --version | grep -q "Universal Ctags"
WORKDIR /src
COPY src/argus/store/migrations/ src/argus/store/migrations/
COPY tests/fixtures/ tests/fixtures/
COPY tests/packs/fixtures/ tests/packs/fixtures/
COPY dotnet/global.json dotnet/Directory.Build.props dotnet/Argus.sln dotnet/
COPY dotnet/src/Argus/Argus.csproj dotnet/src/Argus/
COPY dotnet/tests/Argus.Tests/Argus.Tests.csproj dotnet/tests/Argus.Tests/
RUN dotnet restore dotnet/Argus.sln
COPY dotnet/ dotnet/
# The sqlite-vec release the Python image uses, from its PyPI wheel, checked
# against a pinned SHA-256 before extraction.
RUN case "$(dpkg --print-architecture)" in \
      amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) echo "unsupported arch" >&2; exit 1 ;; \
    esac \
 && dotnet/scripts/fetch-sqlite-vec.sh "$rid"

# ----------------------------------------------------------------- test -----
# The whole xUnit suite against the pinned toolchain. Its receipt is copied
# into the runtime image below, which is what keeps this stage in the build
# graph: without that edge BuildKit would skip it for `--target server`.
FROM build AS test
RUN git config --global user.email build@argus.invalid && git config --global user.name build \
 && dotnet test dotnet/Argus.sln -c Release --no-restore \
 && { echo "suite: passed"; \
      echo "ctags: $(ctags --version | head -1)"; \
      echo "dotnet: $(dotnet --version)"; \
      echo "built: $(date -u +%Y-%m-%dT%H:%M:%SZ)"; } > /src/.build-verified

FROM build AS publish
# Framework-dependent and RID-specific: the image carries only its own
# platform's SQLite, not the thirty the portable layout ships.
RUN case "$(dpkg --print-architecture)" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; esac \
 && dotnet publish dotnet/src/Argus/Argus.csproj -c Release -r "$rid" --self-contained false -o /out \
 && rm -f /out/*.pdb

# -------------------------------------------------------------- runtime -----
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_TAG} AS runtime

ARG ARGUS_VERSION=3.0.0
ARG ARGUS_REVISION=unknown
LABEL org.opencontainers.image.title="argus" \
      org.opencontainers.image.description="Self-hosted code index and documentation MCP server (.NET)" \
      org.opencontainers.image.version="${ARGUS_VERSION}" \
      org.opencontainers.image.revision="${ARGUS_REVISION}" \
      org.opencontainers.image.source="https://github.com/aliGhadyani/hermes-argus" \
      org.opencontainers.image.licenses="MIT"
ENV ARGUS_VERSION="${ARGUS_VERSION}" \
    ARGUS_REVISION="${ARGUS_REVISION}" \
    DOTNET_gcServer=0 \
    DOTNET_GCHeapAffinitizeMask=0 \
    DOTNET_TieredPGO=1

RUN apt-get update \
 && apt-get install -y --no-install-recommends git universal-ctags ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && ctags --version | grep -q "Universal Ctags" \
 && echo "ctags pinned to: $(ctags --version | head -1)"

# The same UID/GID as the Python image, so an existing argus-data volume is
# readable and writable without a chown.
ARG ARGUS_UID=10001
ARG ARGUS_GID=10001
RUN groupadd --gid "${ARGUS_GID}" argus \
 && useradd --uid "${ARGUS_UID}" --gid "${ARGUS_GID}" --create-home --shell /usr/sbin/nologin argus

COPY --from=publish /out /app
RUN ln -s /app/argus /usr/local/bin/argus
COPY --from=test /src/.build-verified /usr/share/argus/build-verified

RUN mkdir -p /var/lib/argus \
 && chown -R argus:argus /var/lib/argus \
 && git config --system --add safe.directory '/var/lib/argus/*' \
 && git config --system --add safe.directory '*'

USER argus
VOLUME ["/var/lib/argus"]
ENTRYPOINT ["argus"]
CMD ["status", "--config", "/etc/argus/config.yaml"]

# --------------------------------------------------------------- server -----
FROM runtime AS server
EXPOSE 7700
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 CMD ["argus", "healthcheck"]
CMD ["serve", "--config", "/etc/argus/config.yaml", "--host", "0.0.0.0", "--port", "7700", \
     "--allowed-host", "argus.internal", "--allowed-host", "argus.internal:*"]
