#!/usr/bin/env bash
# The offline installer, end to end, as a person runs it on a fresh host with no network: a
# privileged container running systemd (tools/offline-e2e/Dockerfile) with its own Docker storage
# and no network at all, the .run copied onto its disk, installed unattended, then checked: every
# service healthy, the site and the API answering, the admin signing in, the admin overview, and
# the installer's own status and verify.
#
#   tools/offline-e2e/run.sh --bundle dist/argus-arena-VERSION-offline.run
#     --first RUN         install RUN first (as it is, a failure allowed), then the bundle over it:
#                         an install cut off by an earlier release, carried on by this one
#     --upgrade-from RUN  install RUN, then upgrade it with the bundle
#     --interrupt-after S an install stopped by Ctrl-C after S seconds first: nothing of it may be left
#                         (no deploy/, containers, images or data folders); then installed again
#     --again             install once more over the finished installation (it must pass again)
#     --reuse             keep the test host's disk from the last run (default: a fresh host each run)
#     --cpu-only          no GPU in the test host (the features that need one are off)
#     --keep              leave the test host running afterwards (docker exec -it NAME bash)
#     --clean             remove the test host's disk (DIR) afterwards
#     --dir DIR           where the test host keeps its disk (default ~/arena-e2e)
#     --name NAME         the test host's container (default arena-e2e)
#
# The test host shares this machine's GPU (through CDI), never its network, its Docker or its stack.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
BUNDLE="" FIRST="" UPGRADE_FROM="" INTERRUPT="" AGAIN=0 REUSE=0 CPU=0 KEEP=0 CLEAN=0 DIR="${HOME}/arena-e2e" NAME=arena-e2e IMAGE=arena-offline-host:1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --bundle) BUNDLE="$2"; shift ;;
    --first) FIRST="$2"; shift ;;
    --upgrade-from) UPGRADE_FROM="$2"; shift ;;
    --interrupt-after) INTERRUPT="$2"; shift ;;
    --cpu-only) CPU=1 ;;
    --again) AGAIN=1 ;;
    --reuse) REUSE=1 ;;
    --keep) KEEP=1 ;;
    --clean) CLEAN=1 ;;
    --dir) DIR="$2"; shift ;;
    --name) NAME="$2"; shift ;;
    -h|--help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
[[ -f "$BUNDLE" ]] || { echo "--bundle names no file: $BUNDLE" >&2; exit 2; }
LOG="$DIR/logs/$(date +%Y%m%d-%H%M%S)"
say() { printf '%s\n' "$*" | tee -a "$LOG/run.log"; }
fail() { say "E2E FAIL: $*"; exit 1; }
H() { docker exec -i "$NAME" "$@"; }

mkdir -p "$DIR"/{docker,containerd,srv,bundle,logs} "$LOG"
# Only a test host this script made is ever replaced.
if docker container inspect "$NAME" >/dev/null 2>&1; then
  [[ "$(docker container inspect -f '{{index .Config.Labels "arena.e2e"}}' "$NAME")" == 1 ]] || fail "a container named $NAME is not a test host: --name another"
  docker rm -f "$NAME" >/dev/null
fi
docker image inspect "$IMAGE" >/dev/null 2>&1 || docker build -q -t "$IMAGE" "$here" >/dev/null || fail "building the test host's image failed"
# A fresh host: nothing of an earlier run on its disk (its files are root's: removed from a container).
if [[ $REUSE -eq 0 ]]; then
  docker run --rm -v "$DIR:/e2e" "$IMAGE" sh -c 'rm -rf /e2e/docker /e2e/containerd /e2e/srv && mkdir -p /e2e/docker /e2e/containerd /e2e/srv' \
    || fail "could not clear the test host's disk in $DIR"
fi

# The bundles copied onto the test host's disk (a hard link when they share a file system).
copy_in() { local f="$1"; ln -f "$f" "$DIR/bundle/" 2>/dev/null || cp -f "$f" "$DIR/bundle/"; [[ -f "$f.sha256" ]] && cp -f "$f.sha256" "$DIR/bundle/"; basename "$f"; }
RUN="$(copy_in "$BUNDLE")"
[[ -n "$FIRST" ]] && FIRST_RUN="$(copy_in "$FIRST")"
[[ -n "$UPGRADE_FROM" ]] && OLD_RUN="$(copy_in "$UPGRADE_FROM")"

gpu=(); [[ $CPU -eq 0 ]] && gpu=(--device nvidia.com/gpu=all)
say "==> the test host ($NAME: no network, its own Docker in $DIR)"
docker run -d --name "$NAME" --label arena.e2e=1 --hostname e2e-host --privileged --cgroupns=private \
  --tmpfs /run --tmpfs /run/lock --network none \
  -v "$DIR/docker:/var/lib/docker" -v "$DIR/containerd:/var/lib/containerd" -v "$DIR/srv:/srv" -v "$DIR/bundle:/bundle" \
  ${gpu[@]+"${gpu[@]}"} "$IMAGE" >/dev/null || fail "the test host did not start"
for _ in $(seq 60); do
  s="$(H systemctl is-system-running 2>/dev/null || true)"
  [[ $s == running || $s == degraded ]] && break
  sleep 1
done
say "  systemd: $s; network: $(H ip -o link show | awk -F': ' '{ print $2 }' | paste -sd' ' -)"
[[ $CPU -eq 0 ]] && say "  GPU: $(H nvidia-smi -L 2>&1 | head -n1)"

# Installs the .run there as a person does: as root, unattended, putting the requirements right.
install_run() {   # install_run RUN.run LABEL [EXTRA...]
  local run="$1" label="$2"; shift 2
  local flags=(--dir /srv/arena --yes --install-requirements) ; [[ $CPU -eq 1 ]] && flags+=(--cpu-only)
  say "==> $label: sh $run install ${flags[*]} $*"
  H sh -c "cd /bundle && sha256sum -c $run.sha256" >> "$LOG/$label.log" 2>&1 || fail "$run does not match its checksum"
  local t0=$SECONDS
  if H sh -c "cd /bundle && sh ./$run install ${flags[*]} $*" > "$LOG/$label.log" 2>&1; then
    say "  $label: done in $(( (SECONDS - t0) / 60 )) min $(( (SECONDS - t0) % 60 )) s (log: $LOG/$label.log)"
    return 0
  fi
  say "  $label: FAILED after $(( SECONDS - t0 )) s; the last lines of $LOG/$label.log:"
  tail -n 25 "$LOG/$label.log" | sed 's/^/    /' | tee -a "$LOG/run.log"
  return 1
}
upgrade_run() {   # upgrade_run RUN.run LABEL
  local run="$1" label="$2"
  say "==> $label: sh $run upgrade --dir /srv/arena --yes"
  H sh -c "cd /bundle && sha256sum -c $run.sha256" >> "$LOG/$label.log" 2>&1 || fail "$run does not match its checksum"
  local t0=$SECONDS
  H sh -c "cd /bundle && sh ./$run upgrade --dir /srv/arena --yes" > "$LOG/$label.log" 2>&1 \
    || { tail -n 25 "$LOG/$label.log" | sed 's/^/    /' | tee -a "$LOG/run.log"; fail "$label failed"; }
  say "  $label: done in $(( (SECONDS - t0) / 60 )) min $(( (SECONDS - t0) % 60 )) s"
}

if [[ -n "$FIRST" ]]; then install_run "$FIRST_RUN" first || say "  (the first install failed, as it may: the bundle carries it on)"; fi

# Stopped part way as a person stops it (Ctrl-C reaches every process of the install): nothing may stay.
interrupted_run() {   # interrupted_run RUN.run SECONDS
  local run="$1" after="$2" flags=(--dir /srv/arena --yes --install-requirements) images_before left
  [[ $CPU -eq 1 ]] && flags+=(--cpu-only)
  images_before="$(H sh -c 'docker images -q 2>/dev/null | sort -u | wc -l' 2>/dev/null || echo 0)"
  say "==> interrupted: sh $run install ${flags[*]}, stopped after $after s"
  H sh -c "cd /bundle && exec setsid sh -c 'echo \$\$ > /tmp/install.pgid; exec sh ./$run install ${flags[*]}'" > "$LOG/interrupted.log" 2>&1 &
  local exec_pid=$!
  sleep "$after"
  H sh -c 'kill -INT -- -$(cat /tmp/install.pgid)' || fail "could not stop the install"
  local t0=$SECONDS
  wait "$exec_pid" || true
  say "  stopped; it ended $(( SECONDS - t0 )) s later; the last lines:"
  tail -n 12 "$LOG/interrupted.log" | sed 's/^/    /' | tee -a "$LOG/run.log"
  left="$(H sh -c 'ls -A /srv/arena 2>/dev/null; ls -A /srv/arena/.arena-install 2>/dev/null; docker ps -aq | wc -l; ls -d /bundle/.argus-arena-unpack* 2>/dev/null' 2>&1)"
  H test ! -e /srv/arena/deploy || fail "deploy/ is left after the stopped install"
  [[ "$(H sh -c 'docker ps -aq | wc -l')" == 0 ]] || fail "containers are left after the stopped install"
  H sh -c 'ls -d /bundle/.argus-arena-unpack* >/dev/null 2>&1' && fail "the .run's unpacked folder is left"
  H sh -c 'ls -A /srv/arena/data 2>/dev/null | grep -q .' && fail "data folders are left"
  local images_after; images_after="$(H sh -c 'docker images -q 2>/dev/null | sort -u | wc -l' 2>/dev/null || echo 0)"
  [[ "$images_after" -le "$images_before" ]] || fail "images are left ($images_before before, $images_after after)"
  say "  nothing of it is left: no deploy/, no containers, no unpacked folder, no data, $images_after image(s) as before ($images_before)"
  say "  left in /srv/arena: $(H sh -c 'find /srv/arena -mindepth 1 -maxdepth 2 | sed "s|/srv/arena/||" | paste -sd" " -' 2>/dev/null)"
}
[[ -n "$INTERRUPT" ]] && interrupted_run "$RUN" "$INTERRUPT"
if [[ -n "$UPGRADE_FROM" ]]; then install_run "$OLD_RUN" old-install || fail "the old release did not install"; upgrade_run "$RUN" upgrade
else install_run "$RUN" install || fail "install failed"; fi

# What a person sees afterwards.
say "==> checks"
H /srv/arena/deploy/scripts/installer.sh status --dir /srv/arena > "$LOG/status.log" 2>&1 || { sed 's/^/    /' "$LOG/status.log" | tail -n 30; fail "status says the installation is not well"; }
say "  status: $(grep -cE '^ *ok ' "$LOG/status.log") ok, $(grep -cE '^ *(FAIL|bad) ' "$LOG/status.log" || true) failing"
H /srv/arena/deploy/scripts/installer.sh verify --dir /srv/arena > "$LOG/verify.log" 2>&1 || { tail -n 20 "$LOG/verify.log"; fail "verify failed"; }
say "  verify: $(tail -n1 "$LOG/verify.log")"
unhealthy="$(H sh -c 'docker ps -a --format "{{.Names}} {{.Status}}"' | grep -vE 'Up .*\(healthy\)|Up [0-9]+ (seconds|minutes|hours)( |$)' || true)"
[[ -z "$unhealthy" ]] || { printf '%s\n' "$unhealthy" | sed 's/^/    /' | tee -a "$LOG/run.log"; fail "containers not up and healthy (above)"; }
say "  containers: $(H docker ps -q | wc -l) up, none unhealthy"
domain="$(H sh -c "sed -n 's/^DOMAIN=//p' /srv/arena/deploy/.env" | tr -d '"')"
port="$(H sh -c "sed -n 's/^HTTPS_PORT=//p' /srv/arena/deploy/.env" | tr -d '"')"; port="${port:-443}"
curl_in() { H curl -sk --resolve "$domain:$port:127.0.0.1" --resolve "gateway.$domain:$port:127.0.0.1" -o /dev/null -w '%{http_code}' "$@"; }
code="$(curl_in "https://$domain:$port/")"; [[ $code == 200 ]] || fail "https://$domain/ answered $code"
say "  the site: 200"
code="$(curl_in "https://$domain:$port/api/auth/me")"; [[ $code == 200 ]] || fail "/api/auth/me answered $code"
# The admin signs in with the first password (.env's, on the test host only; never printed).
code="$(H sh -c "p=\$(sed -n 's/^ADMIN_PASSWORD=//p' /srv/arena/deploy/.env | tr -d '\"'); printf '{\"userName\":\"admin\",\"password\":\"%s\"}' \"\$p\" \
  | curl -sk --resolve '$domain:$port:127.0.0.1' -c /tmp/e2e.cookies -H 'Content-Type: application/json' -H 'X-Requested-With: e2e' \
      -o /dev/null -w '%{http_code}' --data-binary @- 'https://$domain:$port/api/auth/login'")"
[[ $code == 200 ]] || fail "the admin's sign-in answered $code"
H sh -c "curl -sk --resolve '$domain:$port:127.0.0.1' -b /tmp/e2e.cookies -H 'X-Requested-With: e2e' 'https://$domain:$port/api/admin/overview'" > "$LOG/overview.json" \
  || fail "the admin overview did not answer"
grep -q '"services"' "$LOG/overview.json" || fail "the admin overview has no services: $(head -c 300 "$LOG/overview.json")"
say "  the admin signs in; the overview: $(python3 -c "import json,sys; d=json.load(open('$LOG/overview.json')); s=d['services']; print(sum(x['ok'] for x in s), 'of', len(s), 'services ok; not ok:', ', '.join(x['name'] for x in s if not x['ok']) or 'none')" 2>/dev/null)"
if [[ $AGAIN -eq 1 ]]; then
  install_run "$RUN" again || fail "install again over the finished installation failed"
  H /srv/arena/deploy/scripts/installer.sh status --dir /srv/arena > "$LOG/status-again.log" 2>&1 || fail "status after install again"
  [[ "$(curl_in "https://$domain:$port/")" == 200 ]] || fail "the site after install again"
  say "  again: installed, status well, the site 200"
fi
say "E2E PASS ($LOG)"

if [[ $KEEP -eq 0 ]]; then docker rm -f "$NAME" >/dev/null; fi
if [[ $CLEAN -eq 1 ]]; then
  docker rm -f "$NAME" >/dev/null 2>&1 || true
  # The test host's files are root's: removed from a container.
  docker run --rm -v "$DIR:/e2e" "$IMAGE" sh -c 'rm -rf /e2e/docker /e2e/containerd /e2e/srv /e2e/bundle' || true
fi
