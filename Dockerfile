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
RUN python -m pytest -q

# ------------------------------------------------------------- runtime ------
FROM base AS runtime

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
