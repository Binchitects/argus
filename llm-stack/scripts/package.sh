#!/usr/bin/env bash
# Build a drop-in deployable archive of the stack.
#
# What goes in: compose file, configuration templates, scripts, docs, and the
# offline Argus image so a target host needs no build toolchain.
#
# What NEVER goes in: .env (every secret), generated TLS keys, the Authelia user
# database and OIDC clients, issued API keys, model weights, backups. The
# archive is verified against a deny-list before it is written, and the build
# FAILS rather than shipping a secret.
#
#   ./scripts/package.sh
#   ./scripts/package.sh --out /tmp/llmservice.zip
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

OUT=""
INCLUDE_IMAGE=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --out) OUT="$2"; shift 2 ;;
    --no-image) INCLUDE_IMAGE=0; shift ;;
    *) echo "unknown option: $1" >&2; exit 1 ;;
  esac
done

VERSION="$(date +%Y%m%d)"
[[ -n "$OUT" ]] || OUT="dist/llmservice-${VERSION}.zip"
mkdir -p "$(dirname "$OUT")"

# Stage inside the project, not in /tmp: this script hands the path to a
# WINDOWS python, which reads an MSYS /tmp/... path as C:	mp\... and finds
# nothing there - producing a silently empty archive.
STAGE="$ROOT/.pkgstage"
rm -rf "$STAGE"
PKG="$STAGE/llmservice"
mkdir -p "$PKG"

green=$'\033[32m'; red=$'\033[31m'; dim=$'\033[90m'; off=$'\033[0m'
say() { printf '  %s\n' "$1"; }

# ---------------------------------------------------------------------------
say "staging files"
cp docker-compose.yml .env.example "$PKG/"
[[ -f README.md ]] && cp README.md "$PKG/"
[[ -f Makefile ]] && cp Makefile "$PKG/"
[[ -f .gitignore ]] && cp .gitignore "$PKG/"
[[ -f .gitattributes ]] && cp .gitattributes "$PKG/"

mkdir -p "$PKG/scripts" "$PKG/docs" "$PKG/deploy" "$PKG/models" "$PKG/config"
cp scripts/*.sh scripts/*.ps1 scripts/*.py "$PKG/scripts/" 2>/dev/null
mkdir -p "$PKG/scripts/lib" && cp scripts/lib/* "$PKG/scripts/lib/" 2>/dev/null
cp docs/*.md "$PKG/docs/" 2>/dev/null
cp deploy/*.yml "$PKG/deploy/" 2>/dev/null
touch "$PKG/models/.gitkeep"

# Config: templates and provisioning only. Anything generated or secret is
# rebuilt on the target by bootstrap/gen-certs/gen-auth.
for d in prometheus alertmanager grafana loki promtail litellm homepage postgres argus; do
  [[ -d "config/$d" ]] && cp -r "config/$d" "$PKG/config/"
done
mkdir -p "$PKG/config/traefik/dynamic" "$PKG/config/traefik/certs" "$PKG/config/traefik/auth"
cp config/traefik/traefik.yml "$PKG/config/traefik/" 2>/dev/null
cp config/traefik/dynamic/*.yml "$PKG/config/traefik/dynamic/" 2>/dev/null
touch "$PKG/config/traefik/certs/.gitkeep" "$PKG/config/traefik/auth/.gitkeep"

mkdir -p "$PKG/config/authelia"
cp config/authelia/configuration.yml "$PKG/config/authelia/" 2>/dev/null
cp config/authelia/team.yml "$PKG/config/authelia/" 2>/dev/null

# ---------------------------------------------------------------------------
if [[ $INCLUDE_IMAGE -eq 1 ]]; then
  say "including the offline Argus image"
  mkdir -p "$PKG/dist"
  cp dist/argus-*.tar.gz dist/argus-*.sha256 "$PKG/dist/" 2>/dev/null \
    || say "${dim}(no argus tarball found - run docker save first)${off}"
fi

# ---------------------------------------------------------------------------
# Refuse to ship a secret. Checked by NAME and by CONTENT, because a file that
# merely looks harmless can still carry a key.
say "checking for secrets"
FAIL=0

while IFS= read -r f; do
  # .env.example is the template and is meant to ship; anything else
  # matching these names carries real material.
  [ "${f#$PKG/}" = ".env.example" ] && continue
  case "$f" in
    */.env|*/.env.*|*/users.yml|*/clients.yml|*.key|*.pem|*/api-keys.txt|*.htpasswd)
      printf '  %sWOULD SHIP SECRET FILE: %s%s\n' "$red" "${f#$PKG/}" "$off"; FAIL=1 ;;
  esac
done < <(find "$PKG" -type f)

# Content scan: real generated values look nothing like the placeholders.
while IFS= read -r f; do
  case "$f" in *.tar.gz|*.sha256) continue ;; esac
  # Strip placeholder lines first: sk-change-me-please is longer than the
  # pattern minimum but is exactly what SHOULD ship in a template.
  if grep -vE 'change-me|example[.]com|your-|<[a-z-]+>' "$f" 2>/dev/null \
     | grep -qE '(sk-[A-Za-z0-9_-]{16,}|[$]argon2id[$]|BEGIN [A-Z ]*PRIVATE KEY)'; then
    printf '  %sSECRET-LOOKING CONTENT: %s%s\n' "$red" "${f#$PKG/}" "$off"; FAIL=1
  fi
done < <(find "$PKG" -type f)

if [[ $FAIL -ne 0 ]]; then
  printf '  %srefusing to package%s\n' "$red" "$off"
  rm -rf "$STAGE"; exit 1
fi
printf '  %sno secrets found%s\n' "$green" "$off"

# ---------------------------------------------------------------------------
say "writing $OUT"
rm -f "$OUT"
# python's zipfile is used rather than the zip binary: it is present wherever
# the rest of these scripts run, and it sidesteps MSYS path translation on
# Windows, which turns an absolute output path into one zip cannot open.
python - "${STAGE#$ROOT/}" "$OUT" <<'PYEOF'
import pathlib, sys, zipfile
stage, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
out.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for f in sorted(stage.rglob("*")):
        if f.is_file():
            z.write(f, f.relative_to(stage).as_posix())
print("  archive written")
PYEOF

[[ -f "$OUT" ]] && sha256sum "$OUT" > "$OUT.sha256"
rm -rf "$STAGE"

echo
if [[ -f "$OUT" ]]; then
  ls -la "$OUT" | awk '{printf "  %s  %.1f MB\n", $9, $5/1048576}'
  say "sha256: $(cut -d' ' -f1 < "$OUT.sha256")"
fi
echo
say "The recipient runs:"
say "  unzip llmservice-${VERSION}.zip && cd llmservice"
say "  docker load < dist/argus-*.tar.gz"
say "  ./scripts/install-requirements.sh   # or install-requirements.ps1"
say "  ./scripts/bootstrap.sh && ./scripts/gen-auth.sh && ./scripts/up.sh"
echo
