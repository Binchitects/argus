#!/usr/bin/env bash
# Build a drop-in deployable archive of the stack.
#
# What goes in: compose file, configuration templates, scripts, docs, and the
# offline Argus image so a target host needs no build toolchain, and the app's
# sources (built on the target by `docker compose up`).
#
# What NEVER goes in: .env (every secret), generated TLS keys, the imported user
# database and OIDC clients, issued API keys, model weights, backups. The
# archive is verified against a deny-list before it is written, and the build
# FAILS rather than shipping a secret.
#
#   ./scripts/package.sh
#   ./scripts/package.sh --out /tmp/llmservice.zip

# `python` is not a command on a python3-only distro (Ubuntu 26.04 ships no
# alias), so a bare call here dies with "command not found".
PY="${PYTHON:-python3}"

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
cp docker-compose.yml "$PKG/"
mkdir -p "$PKG/env-samples" && cp env-samples/*.env "$PKG/env-samples/"
[[ -f README.md ]] && cp README.md "$PKG/"
[[ -f Makefile ]] && cp Makefile "$PKG/"
[[ -f .gitignore ]] && cp .gitignore "$PKG/"
[[ -f .gitattributes ]] && cp .gitattributes "$PKG/"

mkdir -p "$PKG/scripts" "$PKG/docs" "$PKG/deploy" "$PKG/models" "$PKG/config"
cp scripts/*.sh scripts/*.ps1 scripts/*.py "$PKG/scripts/" 2>/dev/null
mkdir -p "$PKG/scripts/lib" && cp scripts/lib/* "$PKG/scripts/lib/" 2>/dev/null
# The stack's docs live in the repository's single docs tree, one level up from
# this deployment (docs/stack/), not inside stack/ -- so they are gathered from
# there rather than from a docs/ that no longer exists beside this script.
# Discovering that the hard way is why this comment exists: `cp docs/*.md` with
# no docs/ silently produced an archive containing no documentation at all, and
# cp's failure was swallowed by the `2>/dev/null` that is there for optional
# files.
for d in "$ROOT/../docs/stack" "$ROOT/../docs/argus"; do
  [[ -d "$d" ]] || { printf '  %sERROR: %s is missing%s\n' "$red" "$d" "$off"; exit 1; }
  cp -r "$d" "$PKG/docs/"
done
cp deploy/*.yml "$PKG/deploy/" 2>/dev/null
cp deploy/*.py "$PKG/deploy/" 2>/dev/null
# Directories under deploy/ that the compose file BUILDS or BIND-MOUNTS, found
# by reading the compose file rather than by listing them here.
#
# The hardcoded list was `identity-proxy` alone, and the comment above it
# correctly warned that "anything compose builds has to travel with it" --
# while the list itself was already wrong. The archive went out without
# `deploy/admin-panel` (so the admin console could not build on the target) and
# without `deploy/cpu-temp-exporter` (so the `smi` profile had no exporter.py
# to mount). Deriving the list means adding a service cannot silently leave its
# directory behind again.
built_dirs="$(grep -oE '\$\{LLM_DEPLOY_DIR:-\./deploy\}/[A-Za-z0-9._-]+' docker-compose.yml \
             | sed 's|.*/||' | sort -u)"
for d in $built_dirs; do
  if [[ -d "deploy/$d" ]]; then
    cp -r "deploy/$d" "$PKG/deploy/"
  elif [[ -f "deploy/$d" ]]; then
    # A single mounted file (seed-presets.py): directories were all this handled,
    # so a mounted file failed the whole package as "does not exist".
    cp "deploy/$d" "$PKG/deploy/"
  else
    printf '  %sERROR: compose wants deploy/%s but it does not exist%s\n' "$red" "$d" "$off"
    rm -rf "$STAGE"; exit 1
  fi
done
say "deploy/: $(echo $built_dirs | tr '\n' ' ')"
touch "$PKG/models/.gitkeep"

# Config: templates and provisioning only. Anything generated or secret is
# rebuilt on the target by the tls-init and auth-init services.
#
# Copied by TRACKED FILE, not by directory. The services write real secrets
# into these trees at runtime -- config/prometheus/secrets/llamacpp.token,
# config/authelia/users.yml, config/traefik/certs/tls.crt -- and .gitignore
# already names every one of them. `cp -r` ignored that list, so `make package`
# on any machine where the stack had ever run copied a real bearer token into
# the staging tree and then refused to package its own output, with a message
# that reads like the operator did something wrong. Copying what git tracks
# means the two lists cannot drift: the definition of "committed
# configuration" is the same one that decides what this archive carries.
if git rev-parse --git-dir >/dev/null 2>&1; then
  for d in prometheus alertmanager dashboards loki promtail litellm postgres argus; do
    [[ -d "config/$d" ]] || continue
    while IFS= read -r f; do
      mkdir -p "$PKG/$(dirname "$f")"
      cp "$f" "$PKG/$f"
    done < <(git ls-files -- "config/$d")
  done
  say "config copied from tracked files only (generated secrets excluded)"
else
  # Not a checkout: no way to tell committed from generated, so copy the
  # directories and let the secret scan below be the guard. It will refuse if
  # the tree had run.
  say "${dim}(not a git checkout - copying config/ wholesale; the secret scan is the guard)${off}"
  for d in prometheus alertmanager dashboards loki promtail litellm postgres argus; do
    [[ -d "config/$d" ]] && cp -r "config/$d" "$PKG/config/"
  done
fi
mkdir -p "$PKG/config/traefik/dynamic" "$PKG/config/traefik/certs" "$PKG/config/traefik/auth"
# The app (sign-in, admin, dashboards) builds from ../app, so it goes NEXT TO
# the stack folder in the archive: unzipped, llmservice/ and app/ sit side by
# side and compose finds it at its default LLM_APP_DIR. Only tracked sources:
# never bin/, obj/ or node_modules/, and not the tests, which the target never
# runs (one of them holds a password hash made for the test).
( cd "$ROOT/.." && git ls-files app ) | grep -vE '^app/(tests|frontend/e2e|frontend/ci)/' | while read -r f; do
  mkdir -p "$STAGE/$(dirname "$f")" && cp "$ROOT/../$f" "$STAGE/$f"
done
[[ -f "$STAGE/app/Dockerfile" ]] || { printf '  %sERROR: app/ sources missing from the package%s\n' "$red" "$off"; rm -rf "$STAGE"; exit 1; }

cp config/traefik/traefik.yml "$PKG/config/traefik/" 2>/dev/null
cp config/traefik/dynamic/*.yml "$PKG/config/traefik/dynamic/" 2>/dev/null
touch "$PKG/config/traefik/certs/.gitkeep" "$PKG/config/traefik/auth/.gitkeep"

mkdir -p "$PKG/config/authelia/directory"

# ---------------------------------------------------------------------------
if [[ $INCLUDE_IMAGE -eq 1 ]]; then
  # The Argus image is produced by the repository-level release script, which
  # writes to the REPOSITORY root's dist/ (it is not a stack script and does
  # not belong to this directory). So the tarball is looked for in both places
  # rather than in one of them: `scripts/release.sh --offline 2.0.0` from the
  # repository root puts it in ../dist, and a bare `docker save -o dist/...`
  # run from here puts it in ./dist. Checking only this directory silently
  # produced an archive with no image in it, which is a bundle that cannot
  # start on the host it was built for.
  images_dir=""
  for cand in "${IMAGE_DIR:-}" dist ../dist; do
    [[ -n "$cand" ]] || continue
    if compgen -G "$cand/argus-*.tar.gz" >/dev/null 2>&1; then images_dir="$cand"; break; fi
  done
  if [[ -n "$images_dir" ]]; then
    say "including the offline Argus image from ${images_dir}/"
    mkdir -p "$PKG/dist"
    cp "$images_dir"/argus-*.tar.gz "$images_dir"/argus-*.sha256 "$PKG/dist/" 2>/dev/null || true
  else
    say "${dim}(no argus tarball in dist/ or ../dist/ - the archive will need one)${off}"
    say "${dim}(make it with:  ./scripts/release.sh --offline <version>   from the repository root)${off}"
  fi
fi

# ---------------------------------------------------------------------------
# Refuse to ship a secret. Checked by NAME and by CONTENT, because a file that
# merely looks harmless can still carry a key.
say "checking for secrets"
FAIL=0

while IFS= read -r f; do
  # env-samples/*.env are templates with empty SECRETS and are meant to ship;
  # anything else matching these names carries real material.
  case "${f#$STAGE/}" in llmservice/env-samples/*.env) continue ;; esac
  case "$f" in
    */.env|*/.env.*|*/users.yml|*/clients.yml|*.key|*.pem|*/api-keys.txt|*.htpasswd)
      printf '  %sWOULD SHIP SECRET FILE: %s%s\n' "$red" "${f#$STAGE/}" "$off"; FAIL=1 ;;
  esac
done < <(find "$STAGE" -type f)

# Content scan: real generated values look nothing like the placeholders.
while IFS= read -r f; do
  case "$f" in *.tar.gz|*.sha256) continue ;; esac
  # Strip placeholder lines first: sk-change-me-please is longer than the
  # pattern minimum but is exactly what SHOULD ship in a template.
  if grep -vE 'change-me|example[.]com|your-|<[a-z-]+>' "$f" 2>/dev/null \
     | grep -qE '(sk-[A-Za-z0-9_-]{16,}|[$]argon2id[$]|BEGIN [A-Z ]*PRIVATE KEY)'; then
    printf '  %sSECRET-LOOKING CONTENT: %s%s\n' "$red" "${f#$STAGE/}" "$off"; FAIL=1
  fi
done < <(find "$STAGE" -type f)

if [[ $FAIL -ne 0 ]]; then
  printf '  %srefusing to package%s\n' "$red" "$off"
  rm -rf "$STAGE"; exit 1
fi
printf '  %sno secrets found%s\n' "$green" "$off"

# ---------------------------------------------------------------------------
# Make the staged tree readable by the containers that will mount it.
#
# `cp` gives the destination the SOURCE's permission bits, and python's
# zipfile records them in the archive, so this script inherited whatever umask
# the machine that built it happened to have. A developer with `umask 077` --
# or an editor that creates a new config file 0600, which is what happened
# here -- produced an archive whose files are unreadable to the unprivileged
# users the stack runs as: Prometheus (65534) cannot read its own alert rules,
# and the reload fails with `open /etc/prometheus/rules/argus.yml: permission
# denied`, naming a file that plainly exists in the archive.
#
# The airgap bundler has always done this; this script did not. `a+rX` and not
# `a+r`: directories need the execute bit to be traversable, and `X` gives it
# only where it is already set or the entry is a directory.
chmod -R a+rX "$STAGE"

say "writing $OUT"
rm -f "$OUT"
# python's zipfile is used rather than the zip binary: it is present wherever
# the rest of these scripts run, and it sidesteps MSYS path translation on
# Windows, which turns an absolute output path into one zip cannot open.
"$PY" - "${STAGE#$ROOT/}" "$OUT" <<'PYEOF'
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
say "  unzip llmservice-${VERSION}.zip && cd llmservice     # app/ unpacks next to it"
say "  docker load < dist/argus-*.tar.gz"
say "  ./scripts/install-requirements.sh   # or install-requirements.ps1"
say "  cp env-samples/<model>.<gpu>.env .env   # then fill in SECRETS"
say "  docker compose up -d"
say ""
say "  .env is the whole configuration. See README.md."
echo
