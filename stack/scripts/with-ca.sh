#!/usr/bin/env bash
# Run ONE command with this stack's certificate trusted, installing nothing.
#
#   ./scripts/with-ca.sh curl https://api.llm.localhost/v1/models
#   ./scripts/with-ca.sh dsh web
#   ./scripts/with-ca.sh python3 my_client.py
#
#   eval "$(./scripts/with-ca.sh --print)"    # export into this shell instead
#
# ---------------------------------------------------------------------------
# Why a wrapper and not a fix in the stack
# ---------------------------------------------------------------------------
#
# TLS verification happens in the CLIENT. Nothing this stack does on its side
# can make an arbitrary host tool accept a self-signed certificate -- the tool
# has to be told, and every tool wants to be told differently. So there is no
# configuration change that removes this step; there is only a way to make it
# one word instead of a research project.
#
# What this deliberately does NOT do is install the certificate. No system
# trust store, no browser store, no per-tool config file, nothing left behind
# after the command exits. The stack's certificate is generated on its first
# `up` and already sitting in config/traefik/certs/; this points the right
# environment variable at it for the lifetime of one process, and that is all.
# `make down` and `rm -rf` are still a complete uninstall.
#
# Containers need none of this: they mount the certificate and set these same
# variables themselves (see the SSL_CERT_FILE / NODE_EXTRA_CA_CERTS lines in
# docker-compose.yml). This is for the things that run on the HOST -- a
# harness, a curl, an SDK, a test script.
# ---------------------------------------------------------------------------
set -euo pipefail

cd "$(dirname "$0")/.."

CERT_DIR="$PWD/config/traefik/certs"
CA="$CERT_DIR/tls.crt"
BUNDLE="$CERT_DIR/bundle.crt"

if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ]; then
  sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'
  exit 0
fi

if [ ! -s "$CA" ]; then
  printf '\033[31mwith-ca: no certificate at %s\033[0m\n' "$CA" >&2
  printf '         The stack generates it on first start:  make up\n' >&2
  exit 1
fi

cleanup=""
trap '[ -n "$cleanup" ] && rm -f "$cleanup"' EXIT

if [ ! -s "$BUNDLE" ]; then
  # tls-init writes both files on every `up`, so an absent bundle means the
  # stack has not been started since it began exporting one. Build an
  # equivalent here rather than refusing: this script is most useful to
  # someone whose tools are failing right now.
  system_bundle=""
  for candidate in \
      /etc/ssl/certs/ca-certificates.crt \
      /etc/pki/tls/certs/ca-bundle.crt \
      /etc/ssl/cert.pem; do
    if [ -s "$candidate" ]; then system_bundle="$candidate"; break; fi
  done
  # Written to the canonical path, NOT a temp file. `--print` emits these paths
  # for the caller to eval into a long-lived shell, and a temp file would be
  # deleted by the trap below before that shell ever used it -- every tool
  # would then fail to open the bundle, which is worse than not running this.
  # Temp is only the fallback for a read-only certificate directory.
  if { [ -n "$system_bundle" ] && cat "$system_bundle"; cat "$CA"; } \
        > "$BUNDLE" 2>/dev/null; then
    printf 'with-ca: built a combined bundle from %s\n' \
      "${system_bundle:-<no system bundle found>}" >&2
  else
    BUNDLE="$(mktemp)"
    cleanup="$BUNDLE"
    { [ -n "$system_bundle" ] && cat "$system_bundle"; cat "$CA"; } > "$BUNDLE"
    printf 'with-ca: %s is not writable; using %s instead\n' "$CERT_DIR" "$BUNDLE" >&2
    printf 'with-ca: --print would name a file that is deleted on exit; use the wrapper form\n' >&2
  fi
fi

# Two groups, and the split is the whole reason both files are exported.
#
# ADDS to the existing roots, so the stack certificate alone is correct:
#   NODE_EXTRA_CA_CERTS   Node, and therefore the DeepSeek Harness
#
# REPLACES the trust store, so each needs public roots AND the stack's, or the
# command quietly stops trusting the public internet:
#   SSL_CERT_FILE         OpenSSL, and therefore python-requests, httpx, urllib
#   REQUESTS_CA_BUNDLE    python-requests, when it is given its own variable
#   CURL_CA_BUNDLE        curl
#   GIT_SSL_CAINFO        git
STACK_CA="$CA"
COMBINED="$BUNDLE"

emit() {
  printf 'export NODE_EXTRA_CA_CERTS=%q\n' "$STACK_CA"
  printf 'export SSL_CERT_FILE=%q\n' "$COMBINED"
  printf 'export REQUESTS_CA_BUNDLE=%q\n' "$COMBINED"
  printf 'export CURL_CA_BUNDLE=%q\n' "$COMBINED"
  printf 'export GIT_SSL_CAINFO=%q\n' "$COMBINED"
}

if [ "${1:-}" = "--print" ]; then
  emit
  exit 0
fi

if [ "$#" -eq 0 ]; then
  printf '\033[31mwith-ca: no command given.\033[0m\n' >&2
  printf '         ./scripts/with-ca.sh curl https://api.%s/v1/models\n' \
    "${LLM_DOMAIN:-llm.localhost}" >&2
  printf '         ./scripts/with-ca.sh --print     # export into this shell\n' >&2
  exit 2
fi

# eval, not a subshell export: the command is exec'd by this process, so it
# inherits these and they die with it.
eval "$(emit)"

# A note for the tools this cannot help. Go and Java read the OPERATING
# SYSTEM's trust store and honour none of the variables above, so a Go binary
# (Traefik, many CLIs) or a JVM tool still fails here. That is a real
# limit, not an oversight -- say it rather than let it look like this script
# silently did nothing.
printf 'with-ca: trusting %s for this command only (Go/Java tools ignore this)\n' \
  "$STACK_CA" >&2

exec "$@"
