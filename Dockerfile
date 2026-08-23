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
COPY argus/ ./argus/
RUN pip install -e ".[dev]"

COPY tests/ ./tests/

# ONE file, not `COPY deploy/`. A test asserts properties of the reference
# client, so the file must be present or that test fails with a bare
# FileNotFoundError -- which is exactly how this was found, as a build that
# passed 740 tests on the host and failed one only inside the image.
#
# Copying the whole directory would also pull deploy/test-gitlab/seeded.json,
# which holds real GitLab tokens, into an image layer. Layers persist even if
# a later step deletes the file, so this stays a single explicit file.
COPY deploy/agent_client_example.py ./deploy/

RUN python -m pytest -q

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
COPY argus/ ./argus/
RUN pip install .

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

# `argus serve` binds 127.0.0.1 by default (see argus/cli.py) -- that default
# protects a bare-metal or direct-`docker run` deployment from an accidental
# plaintext listener on the LAN. Inside compose, Caddy runs as its own
# container and cannot reach this one's loopback interface -- it has to reach
# this container on the shared compose network -- so the CMD here overrides
# --host to 0.0.0.0 explicitly. That is still safe: this image never
# publishes 7700 to the host (see docker-compose.yml), so 0.0.0.0 only ever
# means "reachable from Caddy inside the compose network," never "reachable
# from the LAN." Caddy is what terminates TLS and is the only container
# whose port reaches outside (see deploy/Caddyfile).
#
# --allowed-host must match what Caddy actually forwards as the Host header,
# not this container's own bind address above. The FastMCP SDK's
# DNS-rebinding protection validates the inbound Host header against an
# allowlist that is fixed once the server object is built (argus/cli.py
# threads --allowed-host into that construction explicitly, precisely
# because the SDK never revisits the allowlist later, e.g. when --host is
# reassigned to 0.0.0.0 as it is above). `deploy/Caddyfile`'s `reverse_proxy`
# is a transparent proxy -- it forwards the client's original Host header
# unchanged (Caddy does not rewrite it to the upstream address by default)
# -- so a developer hitting
# https://argus.internal/mcp arrives here with `Host: argus.internal`. Left
# at the loopback-only default, every one of those requests -- the only
# thing Hermes actually sends -- is rejected with 421 Invalid Host Header,
# even though /healthz (a plain custom Starlette route outside this check)
# looks perfectly healthy the whole time. Two forms are listed because a
# client that includes an explicit port in the URL (https://argus.internal:443/mcp)
# arrives with a Host header carrying that port, which only the wildcard
# form matches -- the bare form only matches when no port is present, which
# is what `hermes mcp add argus --url https://argus.internal` (docs/deployment.md)
# actually sends. Update both if `deploy/Caddyfile`'s site address changes.
EXPOSE 7700
CMD ["serve", "--config", "/etc/argus/config.yaml", "--host", "0.0.0.0", "--port", "7700", \
     "--allowed-host", "argus.internal", "--allowed-host", "argus.internal:*"]
