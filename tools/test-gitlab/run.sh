#!/usr/bin/env bash
# Bring the throwaway GitLab up, seed it, and take it down again.
#
# WHY THIS EXISTS
#
# The fixture used to be started by hand and left running. `restart:
# unless-stopped` made "left running" the default, nothing ever stopped it, and
# measurements on a machine where it had been up five hours and was serving
# nothing at all showed 2.17% CPU and 2.63 GiB resident. On a box whose entire
# purpose is to keep a GPU and 24 cores busy with something else, that is a
# fixture quietly competing with the thing it exists to test.
#
# So the fixture is `restart: "no"` now, and this is its lifecycle. It takes the
# instance down on the way out -- INCLUDING when seeding or verification fails,
# because the run that leaves it up is precisely the one nobody comes back to.
#
#   ./tools/test-gitlab/run.sh                 # up, seed, down
#   ./tools/test-gitlab/run.sh --keep          # leave it up when finished
#   ./tools/test-gitlab/run.sh --skip-verify   # up and seed only
#   ./tools/test-gitlab/run.sh --down          # stop it and delete its data
#
# If the fixture was ALREADY running when this started -- a previous --keep, or
# a session you are in the middle of -- it is left alone on exit and this says
# so. Tearing down something somebody else deliberately started is worse than
# leaving it up.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="$ROOT/tools/test-gitlab/docker-compose.yml"
CONTAINER="argus-test-gitlab"
BASE="http://localhost:8929"

# First boot runs `gitlab-ctl reconfigure` from scratch: on the reference host
# that was several minutes, and a cold image pull adds more. Waiting is the
# whole point -- seeding against a GitLab that is still configuring fails in
# ways that look like a bad seed script.
BOOT_TIMEOUT="${GITLAB_BOOT_TIMEOUT:-900}"

KEEP=0
SKIP_VERIFY=0
DOWN_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --keep)        KEEP=1 ;;
    --skip-verify) SKIP_VERIFY=1 ;;
    --down)        DOWN_ONLY=1 ;;
    -h|--help)     sed -n '2,25p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

say()  { printf '\033[36m%s\033[0m\n' "$*"; }
warn() { printf '\033[33m%s\033[0m\n' "$*" >&2; }
fail() { printf '\033[31m%s\033[0m\n' "$*" >&2; }

compose() { docker compose -f "$COMPOSE_FILE" "$@"; }

running() { [ -n "$(docker ps -q -f "name=^${CONTAINER}$" 2>/dev/null)" ]; }

teardown() {
  # -v on purpose: this instance's volumes are the disposable part, and leaving
  # them behind is how a "temporary" fixture accumulates tens of gigabytes. The
  # seed reproduces them in minutes.
  say "taking the fixture down (and deleting its data)"
  compose down -v --remove-orphans >/dev/null 2>&1 || true
  # The index and the mirrors go with it, because they are derived from the
  # fixture that just stopped existing. Keeping them is a trap rather than a
  # time saving: mirrors are keyed by GitLab PROJECT ID, a fresh instance reuses
  # ids 1..n for whatever it happens to create first, and a cached mirror for id
  # 3 would then be re-fetched from the new project 3's URL while still holding
  # the old project's objects. For a fixture this size, rebuilding costs about
  # two seconds.
  docker volume rm argus-test-work >/dev/null 2>&1 || true
}

if [ "$DOWN_ONLY" = 1 ]; then
  teardown
  say "the fixture is not running"
  exit 0
fi

PRE_EXISTING=0
if running; then
  PRE_EXISTING=1
  say "the fixture is already running; reusing it"
fi

if [ "$PRE_EXISTING" = 0 ]; then
  say "starting the throwaway GitLab (first boot takes several minutes)"
  compose up -d
fi

# ------------------------------------------------------------------ cleanup --
# Registered AFTER the already-running check, so a fixture that was up before
# this script ran is never taken down by it.
cleanup() {
  local rc=$?
  if [ "$KEEP" = 1 ]; then
    warn "leaving the fixture up (--keep). Stop it with:"
    warn "  $0 --down"
  elif [ "$PRE_EXISTING" = 1 ]; then
    warn "leaving the fixture up: it was already running before this script started."
    warn "Stop it with: $0 --down"
  else
    teardown
  fi
  exit $rc
}
trap cleanup EXIT

# --------------------------------------------------------------- wait for -- #
# `/api/v4/version`, NOT `/-/readiness`. GitLab restricts its monitoring
# endpoints to an IP allowlist that is localhost-only by default, so through
# Docker's NAT the source is the bridge gateway and `/-/readiness` answers 404
# from the host -- indistinguishable from "not up yet" if you are only looking
# for 200, so a wait loop on it spins until it times out against an instance
# that has been ready for minutes. `seed.py`'s `wait_for_api` documents the same
# trap; this is the host-side half of it.
#
# 401 is the SUCCESS case: the endpoint is up and asking for a token, which is
# exactly right before seeding. A connection error is the only real failure.
say "waiting for the API (up to ${BOOT_TIMEOUT}s)"
deadline=$(( $(date +%s) + BOOT_TIMEOUT ))
while :; do
  code="$(curl -s -o /dev/null -w '%{http_code}' "$BASE/api/v4/version" 2>/dev/null || true)"
  [ "$code" = "200" ] || [ "$code" = "401" ] && break
  if [ "$(date +%s)" -ge "$deadline" ]; then
    fail "the API did not answer within ${BOOT_TIMEOUT}s (last status: ${code:-none})."
    fail "The container reports healthy well before it is usable, so read the real"
    fail "progress with:"
    fail "  docker compose -f tools/test-gitlab/docker-compose.yml logs -f gitlab"
    exit 1
  fi
  sleep 5
done
say "the API is serving"

# ------------------------------------------------------------------ seed ----
# seed.py needs only the standard library, so a stock python image runs it; it
# also shells out to the docker CLI to run `gitlab-rails runner`, so it needs
# the socket and a docker binary that can reach it.
say "seeding the fixtures"
docker run --rm --user root --network host \
  -e HOME=/tmp -e SEED_PERSON_EMAIL="${SEED_PERSON_EMAIL:-}" \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v "$(command -v docker)":/usr/local/bin/docker \
  -v "$ROOT/tools/test-gitlab:/t" -w /t \
  python:3.13-slim python /t/seed.py

# ---------------------------------------------------------------- verify ----
if [ "$SKIP_VERIFY" = 1 ]; then
  say "skipping verification (--skip-verify)"
  exit 0
fi

# The verifiers (verify_tools.py, the per-tool contract) drove the retired
# Python Argus in-process. Until they drive the .NET service, this harness
# brings the GitLab up and seeds it, which the chat's Argus browser tests need.
warn "not verified: the verifiers are not yet ported to the .NET Argus (see README)."
warn "The GitLab is seeded; tools/test-gitlab/seeded.json has its people and tokens."
