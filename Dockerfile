# syntax=docker/dockerfile:1

# Argus indexer image.
#
# The base tag is pinned deliberately. Argus depends on version-specific
# universal-ctags behaviour that we discovered the hard way:
#
#   * the C/C++ `prototype` kind ships DISABLED by default, so header-only
#     declarations produce no tag unless --kinds-c=+p is passed. Without it the
#     index silently loses most of a C/C++ public API.
#   * a C++ anonymous namespace is reported as a generated identifier such as
#     __anond398a7c10111 -- never the literal "anonymous" -- and its members
#     carry no file-restricted flag.
#
# A host that ships a different ctags changes what gets indexed and what counts
# as a public symbol, with no error. Pinning the base image pins that behaviour,
# and the `test` stage below turns any drift into a failed build rather than a
# quietly wrong index.

# ---------------------------------------------------------------- base ------
FROM python:3.13-slim-bookworm AS base

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      git \
      universal-ctags \
      ripgrep \
      ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 # Fail the build now, loudly, rather than at runtime on a half-built index.
 # Exuberant Ctags has no --output-format=json and cannot be used at all.
 && ctags --version | head -1 \
 && ctags --version | grep -q "Universal Ctags" \
 && echo "ctags pinned to: $(ctags --version | head -1)"

WORKDIR /app

# ---------------------------------------------------------------- test ------
# Runs the full suite against the pinned toolchain. This is the point of the
# stage: if the distro's ctags ever behaves differently from what the parser
# assumes, the build breaks here instead of producing an image that indexes
# incorrectly and reports success.
FROM base AS test

COPY pyproject.toml README.md ./
COPY src/argus/ ./src/argus/
RUN pip install -e ".[dev,pgvector]"

COPY tests/ ./tests/

# ONE file, not `COPY scripts/`. A test asserts properties of the reference
# client, so the file must be present or that test fails with a bare
# FileNotFoundError -- which is exactly how this was found, as a build that
# passed 740 tests on the host and failed one only inside the image.
#
# Copying the whole directory would also pull scripts/test-gitlab/seeded.json,
# which holds real GitLab tokens, into an image layer. Layers persist even if
# a later step deletes the file, so this stays a single explicit file.
COPY scripts/agent_client_example.py ./scripts/

# Same reasoning, one directory over. `check_mounts.py` is the preflight
# bind-mount guard for the stack in stack/, and it is tested here with
# everything else because a bug in it breaks `docker compose up` for every
# non-root operator. Copying stack/ wholesale would put a large tree -- and
# the deployment's .env -- into an image layer for the sake of one file.
COPY stack/scripts/lib/check_mounts.py ./stack/scripts/lib/

# And the same again: `tests/test_seed_presets.py` writes Open WebUI's database
# and is tested here with everything else. Without this line the test stage
# cannot even COLLECT -- FileNotFoundError, exit 2, and the image does not
# build -- which is how this was found, by the airgap bundle failing to make
# its own images. The runner and the test have to travel together.
COPY stack/deploy/seed-presets.py ./stack/deploy/

# And again: `tests/test_acceptance.py` reads the embedder's device out of
# `ollama ps`, and that parsing lives in the acceptance script.
#
# This is the fourth file added here one at a time, each time after the same
# failure: the suite passes on the host and dies inside `docker build` with a
# bare FileNotFoundError and no mention of the Dockerfile. `tests/test_dockerfile.py`
# now checks the COPY list below against what the suite actually reads, so a
# fifth one fails on the developer's own machine and names the line to add.
COPY stack/scripts/acceptance.py ./stack/scripts/

RUN python -m pytest -q \
 && { echo "suite: passed"; \
      echo "ctags: $(ctags --version | head -1)"; \
      echo "built: $(date -u +%Y-%m-%dT%H:%M:%SZ)"; } > /app/.build-verified

# The receipt above exists to be COPYed into `runtime`. That copy is what puts
# this stage in the build graph: BuildKit builds only what the target depends
# on, and nothing depended on `test`, so `--target server` skipped the suite
# entirely and the whole toolchain-drift guard above never fired. Found on the
# 1.1.0 release build -- zero pytest lines in the log, and the image shipped
# anyway. Deleting that COPY silently restores the hole.

# ------------------------------------------------------------- runtime ------
FROM base AS runtime

# Provenance, so a running container can say what it is. `pyproject.toml` is
# inside the image but `docker inspect` cannot read it, and the question asked
# of a deployed container is always "which build is this" -- answering it by
# exec'ing in and grepping is the wrong shape.
#
# ARGUS_REVISION is passed by the release build; it is the git commit, which
# is the only identifier that stays exact when a version is built twice.
ARG ARGUS_VERSION=1.1.0
ARG ARGUS_REVISION=unknown
LABEL org.opencontainers.image.title="argus" \
      org.opencontainers.image.description="Self-hosted code index and documentation MCP server" \
      org.opencontainers.image.version="${ARGUS_VERSION}" \
      org.opencontainers.image.revision="${ARGUS_REVISION}" \
      org.opencontainers.image.source="https://github.com/aliGhadyani/hermes-argus" \
      org.opencontainers.image.licenses="MIT"
ENV ARGUS_VERSION="${ARGUS_VERSION}" \
    ARGUS_REVISION="${ARGUS_REVISION}"

# Fixed UID/GID so a bind-mounted data directory can be chowned predictably on
# the host. Override at build time if it collides with an existing account.
ARG ARGUS_UID=10001
ARG ARGUS_GID=10001
RUN groupadd --gid "${ARGUS_GID}" argus \
 && useradd --uid "${ARGUS_UID}" --gid "${ARGUS_GID}" --create-home --shell /usr/sbin/nologin argus

COPY pyproject.toml README.md ./
COPY src/argus/ ./src/argus/
# The host's file modes ride along with the copy, and a source file written
# under a restrictive umask (0600, which is what an editor or tool honouring
# umask 077 produces) stays unreadable to the `argus` user this image switches
# to below. That failure is deferred and silent, which is why it gets a
# build-time fix rather than a note: `serve` starts, /healthz answers 200,
# every container reports healthy, and only a request that happens to import
# that module dies -- with a PermissionError from inside the import machinery.
# Observed exactly that here after a new module was added by a tool that wrote
# it 0600: `argus serve` came up healthy and the indexer could not import it.
RUN chmod -R a+rX ./src/argus
# [pgvector]: ships the optional Postgres backend so a deployment can select
# it with ARGUS_VECTOR_BACKEND=pgvector without rebuilding. psycopg is ~10 MB
# and imported lazily, so it costs nothing on the default sqlite-vec path.
RUN pip install ".[pgvector]"

# NOT decorative, and not safe to drop. This is the only edge from `runtime`
# to `test`, and it is what makes the suite run for every image built from
# here -- `server` included. Without it BuildKit prunes `test` from the graph
# and the pinned-ctags guard protects nothing.
#
# The file is three lines. `docker run --entrypoint cat argus:TAG
# /usr/share/argus/build-verified` says which ctags the suite passed against,
# which is the question the pin exists to answer.
COPY --from=test /app/.build-verified /usr/share/argus/build-verified

# The indexer runs git against bind-mounted repositories whose owner UID will
# not match the container user. Without this, git refuses with "detected
# dubious ownership in repository". The mirrors are ours and are never executed
# from, so scoping the exemption to the data directory is safe.
RUN mkdir -p /var/lib/argus \
 && chown -R argus:argus /var/lib/argus \
 && git config --system --add safe.directory '/var/lib/argus/*' \
 && git config --system --add safe.directory '*'

USER argus
VOLUME ["/var/lib/argus"]

# Not a daemon — the indexer is a batch job. `status` is a safe default that
# touches nothing, so a bare `docker run` cannot start an unintended index.
ENTRYPOINT ["argus"]
CMD ["status", "--config", "/etc/argus/config.yaml"]

# ------------------------------------------------------------- server -------
# The MCP retrieval server: unlike the indexer above, this IS a long-running
# daemon and is meant to be started by `docker compose up` (see
# docker-compose.yml). It extends `runtime` rather than duplicating its
# apt/user/git setup -- same pinned ctags base, same non-root user, same
# /var/lib/argus volume, just a different entrypoint and exposed port.
FROM runtime AS server

# `argus serve` binds 127.0.0.1 by default (see src/argus/cli.py) -- that
# default protects a bare-metal or direct-`docker run` deployment from an
# accidental plaintext listener on the LAN. Behind a reverse proxy the proxy
# runs as its own container and cannot reach this one's loopback interface --
# it has to reach this container on the shared compose network -- so the CMD
# here overrides --host to 0.0.0.0 explicitly. That is still safe: this image
# never publishes 7700 to the host (see stack/docker-compose.yml), so 0.0.0.0
# only ever means "reachable from the proxy inside the compose network", never
# "reachable from the LAN". The proxy is what terminates TLS and is the only
# container whose port reaches outside.
#
# --allowed-host must match what the proxy actually forwards as the Host
# header, not this container's own bind address above. The FastMCP SDK's
# DNS-rebinding protection validates the inbound Host header against an
# allowlist that is fixed once the server object is built (src/argus/cli.py
# threads --allowed-host into that construction explicitly, precisely because
# the SDK never revisits the allowlist later, e.g. when --host is reassigned
# to 0.0.0.0 as it is above). A transparent reverse proxy forwards the
# client's original Host header unchanged, so a request to
# https://argus.<domain>/mcp arrives here with `Host: argus.<domain>`. Left at
# the loopback-only default, every one of those requests is rejected with
# 421 Invalid Host Header, even though /healthz (a plain custom Starlette
# route outside this check) looks perfectly healthy the whole time.
#
# These two are the BARE-IMAGE defaults, and `argus.internal` is a
# placeholder. The stack overrides them in stack/docker-compose.yml with
# --allowed-host=argus.${LLM_DOMAIN}, which is the form a real deployment
# uses. Both the bare and the wildcard form are listed because a client that
# includes an explicit port in its URL (https://argus.internal:443/mcp)
# arrives with a Host header carrying that port, which only the wildcard form
# matches -- the bare form matches only when no port is present.
EXPOSE 7700
CMD ["serve", "--config", "/etc/argus/config.yaml", "--host", "0.0.0.0", "--port", "7700", \
     "--allowed-host", "argus.internal", "--allowed-host", "argus.internal:*"]
