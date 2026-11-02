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
#     --upgrade-from RUN  install RUN (with --hosts), write what people keep there
#                         (tools/offline-e2e/data.py: a person, a chat, a file attached, a key, a setting), then upgrade
#                         it with the bundle: everything below is checked, that data must be there as it was, and
#                         the model library's owner as it was
#     --fail-upgrade      the new release made unhealthy as the upgrade starts it (the web's health check fails while
#                         a file is there, taken away once it rolls back): it must roll back by itself (exit 4) to
#                         RUN's release, running, with the data
#     --rollback          after the upgrade, upgrade --rollback: RUN's release again, running, with the data
#     --interrupt-upgrade-at TEXT  an upgrade stopped once its output shows TEXT (--interrupt-upgrade-after S
#                         seconds later), and run again: repeated, each run is stopped at the next TEXT, and must
#                         leave its record; the last runs to the end (nothing of the stopped runs may be left)
#     --signal SIG        how those runs are stopped: INT (Ctrl-C, the default), HUP (a dropped session), KILL (the
#                         installer killed outright: a crash, a lost machine)
#     --cut-off-by RUN    those stopped upgrades are an older release's bundle (RUN): the bundle carries on what it
#                         left (a hotfix on a host whose upgrade to the release before it was cut off)
#     --upgrade-timeout T the upgrade's --timeout (minutes, or 90s)
#     --interrupt-after S an install stopped by Ctrl-C after S seconds first: nothing of it may be left
#                         (no deploy/, containers, images or data folders); then installed again
#     --interrupt-at TEXT stopped once its output shows TEXT (then --interrupt-after S seconds more)
#     --again             install once more over the finished installation (it must pass again)
#     --model FILE        a chat model (.gguf) copied onto the test host and installed with it
#                         (--models-dir, --model, --hosts); then the functional test
#                         (deploy/scripts/functional-test.py) runs there: as the admin and a person
#                         it makes, every check through HTTPS, a chat answered by that model
#     --reuse             keep the test host's disk from the last run (default: a fresh host each run)
#     --cpu-only          no GPU in the test host (the features that need one are off)
#     --with LIST         features on beside the defaults, as installer.sh --with takes them (all: every one); with
#                         argus or the scanners, installed with a GitLab address where nothing answers; never
#                         power-limits: the test host shares this machine's GPU and CPU, whose caps are the machine's
#     --keep              leave the test host running afterwards (docker exec -it NAME bash)
#     --clean             remove the test host's disk (DIR) afterwards
#     --dir DIR           where the test host keeps its disk (default ~/arena-e2e)
#     --name NAME         the test host's container (default arena-e2e)
#
# The test host shares this machine's GPU (through CDI), never its network, its Docker or its stack.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
BUNDLE="" FIRST="" MODEL="" WITH="" UPGRADE_FROM="" INTERRUPT="" INTERRUPT_AT="" AGAIN=0 REUSE=0 CPU=0 KEEP=0 CLEAN=0 DIR="${HOME}/arena-e2e" NAME=arena-e2e IMAGE=arena-offline-host:1
FAIL_UPGRADE=0 ROLLBACK=0 SIGNAL=INT UPGRADE_TIMEOUT="" STOPS=() CUT_BY="" STOP_AFTER=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --bundle) BUNDLE="$2"; shift ;;
    --first) FIRST="$2"; shift ;;
    --model) MODEL="$2"; shift ;;
    --with) WITH="$2"; shift ;;
    --upgrade-from) UPGRADE_FROM="$2"; shift ;;
    --interrupt-after) INTERRUPT="$2"; shift ;;
    --interrupt-at) INTERRUPT_AT="$2"; shift ;;
    --fail-upgrade) FAIL_UPGRADE=1 ;;
    --rollback) ROLLBACK=1 ;;
    --interrupt-upgrade-at) STOPS+=("$2"); shift ;;
    --interrupt-upgrade-after) STOP_AFTER="$2"; shift ;;
    --signal) SIGNAL="$2"; shift; [[ $SIGNAL =~ ^(INT|HUP|TERM|KILL)$ ]] || { echo "--signal is INT, HUP, TERM or KILL" >&2; exit 2; } ;;
    --upgrade-timeout) UPGRADE_TIMEOUT="$2"; shift ;;
    --cut-off-by) CUT_BY="$2"; shift ;;
    --cpu-only) CPU=1 ;;
    --again) AGAIN=1 ;;
    --reuse) REUSE=1 ;;
    --keep) KEEP=1 ;;
    --clean) CLEAN=1 ;;
    --dir) DIR="$2"; shift ;;
    --name) NAME="$2"; shift ;;
    -h|--help) sed -n '2,44p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
[[ -f "$BUNDLE" ]] || { echo "--bundle names no file: $BUNDLE" >&2; exit 2; }
[[ $WITH == all ]] && WITH="$(sed -n 's/^\([a-z0-9-]*\) *|.*/\1/p' "$here/../../deploy/features" | paste -sd, -)"
# A test host shares this machine's GPU and CPU: its own power-limits would set their power caps and temperature policy
# for the whole machine, over the machine's own (it put the GPU back to its default 350 W while a test ran). It stays off.
WITH="$(tr ',' '\n' <<< "$WITH" | grep -vx power-limits | paste -sd, -)"
HOST_FLAGS=(--without power-limits)
[[ -z "$MODEL" || ( -f "$MODEL" && "$MODEL" == *.gguf ) ]] || { echo "--model names no .gguf file: $MODEL" >&2; exit 2; }
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
  docker run --rm -v "$DIR:/e2e" "$IMAGE" sh -c 'rm -rf /e2e/docker /e2e/containerd /e2e/srv /e2e/bundle/.argus-arena-unpack.* && mkdir -p /e2e/docker /e2e/containerd /e2e/srv' \
    || fail "could not clear the test host's disk in $DIR"
fi

# The bundles copied onto the test host's disk (a hard link when they share a file system).
copy_in() { local f="$1"; ln -f "$f" "$DIR/bundle/" 2>/dev/null || cp -f "$f" "$DIR/bundle/"; [[ -f "$f.sha256" ]] && cp -f "$f.sha256" "$DIR/bundle/"; basename "$f"; }
RUN="$(copy_in "$BUNDLE")"
[[ -n "$FIRST" ]] && FIRST_RUN="$(copy_in "$FIRST")"
[[ -n "$UPGRADE_FROM" ]] && OLD_RUN="$(copy_in "$UPGRADE_FROM")"
STOP_RUN="$RUN"; [[ -n "$CUT_BY" ]] && STOP_RUN="$(copy_in "$CUT_BY")"
# The model copied (never linked: the installer may own it), from a folder the copy only reads.
MODEL_FLAGS=()
if [[ -n "$MODEL" ]]; then
  docker run --rm -v "$DIR:/e2e" -v "$(cd "$(dirname "$MODEL")" && pwd):/src:ro" "$IMAGE" \
    sh -c "mkdir -p /e2e/srv/models && cp /src/$(basename "$MODEL") /e2e/srv/models/" || fail "could not copy $MODEL onto the test host"
  MODEL_FLAGS=(--models-dir /srv/models --model "$(basename "$MODEL")" --hosts)
fi
# Argus and the scanners need a GitLab to install with: an address where nothing answers, and a made-up token (the
# test host has no network). Every install takes it.
GITLAB_FLAGS=()
if [[ ",$WITH," == *,argus,* || ",$WITH," == *,scanners,* ]]; then
  (umask 077; openssl rand -hex 20 > "$DIR/bundle/e2e-gitlab-token")
  GITLAB_FLAGS=(--gitlab-url http://host.docker.internal:8929 --gitlab-token-file /bundle/e2e-gitlab-token)
fi
# An upgrade's old release: the names in /etc/hosts (data.py reaches the stack by them).
OLD_FLAGS=()
if [[ -n "$UPGRADE_FROM" ]]; then
  OLD_FLAGS=(${MODEL_FLAGS[@]+"${MODEL_FLAGS[@]}"}); [[ " ${OLD_FLAGS[*]} " == *" --hosts "* ]] || OLD_FLAGS+=(--hosts)
  cp -f "$here/data.py" "$DIR/bundle/data.py"
  rm -f "$DIR/bundle/data.json"
  OLD_VERSION="$(basename "$UPGRADE_FROM" | sed -n 's/^argus-arena-\(.*\)-offline\.run$/\1/p')"
fi
NEW_VERSION="$(basename "$BUNDLE" | sed -n 's/^argus-arena-\(.*\)-offline\.run$/\1/p')"

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
  local flags=(--dir /srv/arena --yes --install-requirements ${GITLAB_FLAGS[@]+"${GITLAB_FLAGS[@]}"} "${HOST_FLAGS[@]}") ; [[ $CPU -eq 1 ]] && flags+=(--cpu-only)
  [[ -n "$WITH" ]] && flags+=(--with "$WITH")
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
# The bundle's upgrade on the test host, in a process group of its own, its output watched: with --fail-upgrade the
# fault is on once it writes the new release's files and off once it rolls back; with STOP it is stopped (SIGNAL)
# once its output shows STOP, --interrupt-upgrade-after seconds later. Its exit status in RC.
RC=0
upgrade_run() {   # upgrade_run RUN.run LABEL STOP [EXTRA...]
  local run="$1" label="$2" stop="$3" pid stopped=0 t0
  shift 3
  local args=(upgrade --dir /srv/arena --yes ${UPGRADE_TIMEOUT:+--timeout "$UPGRADE_TIMEOUT"} "$@")
  say "==> $label: sh $run ${args[*]}$([[ -n "$stop" ]] && echo ", stopped (SIG$SIGNAL) once it says \"$stop\"")"
  H sh -c "cd /bundle && sha256sum -c $run.sha256" > "$LOG/$label.log" 2>&1 || fail "$run does not match its checksum"
  t0=$SECONDS
  H sh -c "cd /bundle && exec setsid -w sh -c 'echo \$\$ > /tmp/upgrade.pgid; exec sh ./$run ${args[*]}'" >> "$LOG/$label.log" 2>&1 &
  pid=$!
  while kill -0 "$pid" 2>/dev/null; do
    if [[ $FAIL_UPGRADE -eq 1 ]]; then
      if grep -q '^==> rolling back to' "$LOG/$label.log"; then H rm -f /srv/e2e-fault/unhealthy
      elif grep -q '^==> deploy/ (' "$LOG/$label.log"; then H touch /srv/e2e-fault/unhealthy; fi
    fi
    if [[ -n "$stop" && $stopped -eq 0 ]] && grep -qF -- "$stop" "$LOG/$label.log"; then
      sleep "$STOP_AFTER"
      # The whole process group, as a terminal's Ctrl-C or hang-up reaches it (bash's kill: dash's takes no -- before a group).
      H bash -c "kill -$SIGNAL -- -\"\$(cat /tmp/upgrade.pgid)\"" || fail "could not stop the upgrade"
      say "  it says \"$stop\": stopped (SIG$SIGNAL) $STOP_AFTER s later"
      stopped=1
    fi
    sleep 2
  done
  wait "$pid" && RC=0 || RC=$?
  [[ -z "$stop" || $stopped -eq 1 ]] || fail "$label ended (exit $RC) before it said: $stop"
  say "  $label: exit $RC after $(( (SECONDS - t0) / 60 )) min $(( (SECONDS - t0) % 60 )) s; the last lines:"
  tail -n "$([[ $RC -eq 0 ]] && echo 3 || echo 25)" "$LOG/$label.log" | sed 's/^/    /' | tee -a "$LOG/run.log"
}
# What people keep (tools/offline-e2e/data.py), written or looked for on the test host: in the stack's own Python
# image, through HTTPS by the names --hosts wrote.
data_run() {   # data_run seed|check LABEL
  say "==> data ($2): data.py $1"
  if H docker run --rm --network host -v /etc/hosts:/etc/hosts:ro -v /srv/arena/deploy:/srv/arena/deploy:ro -v /bundle:/e2e \
      python:3.13-slim python3 -I /e2e/data.py "$1" /e2e/data.json > "$LOG/data-$2.log" 2>&1; then
    say "  $(tail -n1 "$LOG/data-$2.log")"
  else
    sed 's/^/    /' "$LOG/data-$2.log" | tail -n 10 | tee -a "$LOG/run.log"
    fail "the data $([[ $1 == seed ]] && echo "was not written" || echo "is not as it was") ($2)"
  fi
}
# The fault for --fail-upgrade: the web healthy only while /srv/e2e-fault/unhealthy is not there, in the
# installation's own override (kept by the upgrade and its rollback, as a host's override is).
fault_override() {
  local f=/srv/arena/deploy/docker-compose.override.yml now
  now="$(H sh -c "cat $f 2>/dev/null || true")"
  H mkdir -p /srv/e2e-fault
  printf '%s\n' "$now" | awk '
    function web() {
      print "  web:"
      print "    volumes: [\"/srv/e2e-fault:/e2e-fault:ro\"]"
      print "    healthcheck: { test: [\"CMD-SHELL\", \"test ! -e /e2e-fault/unhealthy && wget -qO /dev/null http://127.0.0.1:8080/healthz\"], interval: 5s, timeout: 3s, retries: 2, start_period: 5s }"
      done = 1
    }
    /^services: *\{\} *$/ { print "services:"; web(); next }
    /^services: *$/ { print; web(); next }
    { print }
    END { if (!done) { print "services:"; web() } }' | H sh -c "cat > $f" || fail "could not write the fault into $f"
  say "  the fault: the web's health check in $f fails while /srv/e2e-fault/unhealthy is there"
}

if [[ -n "$FIRST" ]]; then install_run "$FIRST_RUN" first || say "  (the first install failed, as it may: the bundle carries it on)"; fi

# Stopped part way as a person stops it (Ctrl-C reaches every process of the install): nothing may stay.
interrupted_run() {   # interrupted_run RUN.run SECONDS
  local run="$1" after="$2" flags=(--dir /srv/arena --yes --install-requirements ${GITLAB_FLAGS[@]+"${GITLAB_FLAGS[@]}"} "${HOST_FLAGS[@]}") images_before
  [[ $CPU -eq 1 ]] && flags+=(--cpu-only)
  [[ -n "$WITH" ]] && flags+=(--with "$WITH")
  images_before="$(H sh -c 'docker images -q 2>/dev/null | sort -u | wc -l' 2>/dev/null || echo 0)"
  say "==> interrupted: sh $run install ${flags[*]}, stopped after $after s"
  H sh -c "cd /bundle && exec setsid -w sh -c 'echo \$\$ > /tmp/install.pgid; exec sh ./$run install ${flags[*]}'" > "$LOG/interrupted.log" 2>&1 &
  local exec_pid=$!
  if [[ -n "$INTERRUPT_AT" ]]; then
    until grep -qF -- "$INTERRUPT_AT" "$LOG/interrupted.log" 2>/dev/null; do
      kill -0 "$exec_pid" 2>/dev/null || fail "the install ended before it said: $INTERRUPT_AT"
      sleep 2
    done
    say "  it says \"$INTERRUPT_AT\": stopped $after s later"
  fi
  sleep "$after"
  # Ctrl-C reaches the whole process group (bash's kill: dash's takes no -- before a group).
  H bash -c 'kill -INT -- -"$(cat /tmp/install.pgid)"' || fail "could not stop the install"
  local t0=$SECONDS
  wait "$exec_pid" || true
  say "  stopped; it ended $(( SECONDS - t0 )) s later; the last lines:"
  tail -n 12 "$LOG/interrupted.log" | sed 's/^/    /' | tee -a "$LOG/run.log"
  H test ! -e /srv/arena/deploy || fail "deploy/ is left after the stopped install"
  [[ "$(H sh -c 'docker ps -aq | wc -l')" == 0 ]] || fail "containers are left after the stopped install"
  H sh -c 'ls -d /bundle/.argus-arena-unpack* >/dev/null 2>&1' && fail "the .run's unpacked folder is left"
  H sh -c 'ls -A /srv/arena/data 2>/dev/null | grep -q .' && fail "data folders are left"
  local images_after; images_after="$(H sh -c 'docker images -q 2>/dev/null | sort -u | wc -l' 2>/dev/null || echo 0)"
  [[ "$images_after" -le "$images_before" ]] || fail "images are left ($images_before before, $images_after after)"
  say "  nothing of it is left: no deploy/, no containers, no unpacked folder, no data, $images_after image(s) as before ($images_before)"
  say "  left in /srv/arena: $(H sh -c 'find /srv/arena -mindepth 1 -maxdepth 2 | sed "s|/srv/arena/||" | paste -sd" " -' 2>/dev/null)"
}
[[ -n "$INTERRUPT" || -n "$INTERRUPT_AT" ]] && interrupted_run "$RUN" "${INTERRUPT:-0}"
EXPECT="$NEW_VERSION"
if [[ -n "$UPGRADE_FROM" ]]; then
  install_run "$OLD_RUN" old-install ${OLD_FLAGS[@]+"${OLD_FLAGS[@]}"} || fail "the old release did not install"
  data_run seed "$OLD_VERSION"
  # The model library's owner is the host's (install asked, and kept it): an upgrade keeps it.
  models_owner=""; [[ -n "$MODEL" ]] && models_owner="$(H stat -c %u:%g /srv/models)"
  [[ $FAIL_UPGRADE -eq 1 ]] && fault_override
  n=0
  for stop in ${STOPS[@]+"${STOPS[@]}"}; do
    n=$((n + 1))
    upgrade_run "$STOP_RUN" "upgrade-stopped-$n" "$stop"
    H test -f /srv/arena/.arena-install/upgrade || fail "the stopped upgrade left no record to carry on from"
  done
  # Stopped by an older release's bundle: what it did to the model library is that release's (7.7.0 took it).
  [[ -n "$CUT_BY" && -n "$models_owner" ]] && models_owner="$(H stat -c %u:%g /srv/models)"
  upgrade_run "$RUN" upgrade ""
  H sh -c 'ls -d /bundle/.argus-arena-unpack* >/dev/null 2>&1' && fail "an unpacked bundle is left in /bundle: $(H sh -c 'ls -d /bundle/.argus-arena-unpack*' | paste -sd' ' -)"
  [[ -z "$models_owner" || "$(H stat -c %u:%g /srv/models)" == "$models_owner" ]] || fail "the upgrade changed the model library's owner ($models_owner before)"
  if [[ $FAIL_UPGRADE -eq 1 ]]; then
    [[ $RC -eq 4 ]] || fail "the failed upgrade did not roll back by itself (exit $RC, not 4)"
    H test ! -e /srv/arena/.arena-install/upgrade || fail "the upgrade's record is left after its rollback"
    EXPECT="$OLD_VERSION"
  else
    [[ $RC -eq 0 ]] || fail "the upgrade failed (exit $RC)"
  fi
  data_run check "$([[ $FAIL_UPGRADE -eq 1 ]] && echo "after its rollback" || echo "after the upgrade")"
  if [[ $ROLLBACK -eq 1 ]]; then
    upgrade_run "$RUN" rollback "" --rollback
    [[ $RC -eq 0 ]] || fail "upgrade --rollback failed (exit $RC)"
    EXPECT="$OLD_VERSION"
    data_run check "after the rollback"
  fi
else install_run "$RUN" install ${MODEL_FLAGS[@]+"${MODEL_FLAGS[@]}"} || fail "install failed"; fi

# What a person sees afterwards.
say "==> checks"
H /srv/arena/deploy/scripts/installer.sh status --dir /srv/arena > "$LOG/status.log" 2>&1 || { sed 's/^/    /' "$LOG/status.log" | tail -n 30; fail "status says the installation is not well"; }
say "  status: $(grep -cE '^  [a-z0-9-]+ +running' "$LOG/status.log") services running, $(grep -cE '^ *FAIL ' "$LOG/status.log" || true) failing; $(grep -m1 '(says ' "$LOG/status.log" | sed 's/.*(says /the app says /; s/)$//')"
# The release that should run: the bundle's, or after a rollback the old one's (the installer's record and the app's word).
if [[ -n "$EXPECT" ]]; then
  grep -q "^  installed: $EXPECT " "$LOG/status.log" || fail "status says $(grep -m1 '^  installed: ' "$LOG/status.log"), not $EXPECT"
  grep -qE "^  app .*\(says $EXPECT\)" "$LOG/status.log" || fail "the app does not say $EXPECT: $(grep -m1 '^  app ' "$LOG/status.log")"
  H test ! -e /srv/arena/.arena-install/upgrade || fail "an upgrade's record is left: $(H cat /srv/arena/.arena-install/upgrade | grep -v '^excused=' | paste -sd' ' -)"
  say "  $EXPECT runs: the installer's record and the app say so; no upgrade record is left"
fi
H /srv/arena/deploy/scripts/installer.sh verify --dir /srv/arena > "$LOG/verify.log" 2>&1 || { tail -n 20 "$LOG/verify.log"; fail "verify failed"; }
say "  verify: $(tail -n1 "$LOG/verify.log")"
# Every container up, and healthy where it has a health check (one still starting is given 2 minutes).
for _ in $(seq 24); do
  H sh -c 'docker ps -a --format "{{.Names}} {{.Status}}"' | grep -q '(health: starting)' || break
  sleep 5
done
unhealthy="$(H sh -c 'docker ps -a --format "{{.Names}} {{.Status}}"' | grep -vE ' Up ' ; H sh -c 'docker ps -a --format "{{.Names}} {{.Status}}"' | grep -E 'unhealthy|health: starting|Restarting' || true)"
[[ -z "$unhealthy" ]] || { printf '%s\n' "$unhealthy" | sed 's/^/    /' | tee -a "$LOG/run.log"; fail "containers not up and healthy (above)"; }
say "  containers: $(H docker ps -q | wc -l) up, none unhealthy"
if [[ -n "$FIRST" ]]; then
  # What the first release's cut-off install loaded of its own is gone (what this one carries is kept).
  old_version="$(basename "$FIRST" | sed -n 's/^argus-arena-\(.*\)-offline\.run$/\1/p')"
  left_images="$(H sh -c "docker images --format '{{.Repository}}:{{.Tag}}' | grep -E ':$old_version\$' || true")"
  [[ -z "$left_images" ]] || fail "the cut-off $old_version install's images are left: $(printf '%s' "$left_images" | paste -sd' ' -)"
  say "  none of $old_version's own images is left"
fi
domain="$(H sh -c "sed -n 's/^DOMAIN=//p' /srv/arena/deploy/.env" | tr -d '"')"
port="$(H sh -c "sed -n 's/^HTTPS_PORT=//p' /srv/arena/deploy/.env" | tr -d '"')"; port="${port:-443}"
curl_in() { H curl -sk --resolve "$domain:$port:127.0.0.1" --resolve "gateway.$domain:$port:127.0.0.1" -o /dev/null -w '%{http_code}' "$@"; }
code="$(curl_in "https://$domain:$port/")"; [[ $code == 200 ]] || fail "https://$domain/ answered $code"
say "  the site: 200"
# Nobody signed in yet: the API says so (401), it does not fail.
code="$(curl_in "https://$domain:$port/api/auth/me")"; [[ $code == 401 || $code == 200 ]] || fail "/api/auth/me answered $code"
# The admin signs in with the first password (.env's, on the test host only; never printed).
code="$(H sh -c "p=\$(sed -n 's/^ADMIN_PASSWORD=//p' /srv/arena/deploy/.env | tr -d '\"'); printf '{\"userName\":\"admin\",\"password\":\"%s\"}' \"\$p\" \
  | curl -sk --resolve '$domain:$port:127.0.0.1' -c /tmp/e2e.cookies -H 'Content-Type: application/json' -H 'X-Requested-With: e2e' \
      -o /dev/null -w '%{http_code}' --data-binary @- 'https://$domain:$port/api/auth/login'")"
[[ $code == 200 ]] || fail "the admin's sign-in answered $code"
H sh -c "curl -sk --resolve '$domain:$port:127.0.0.1' -b /tmp/e2e.cookies -H 'X-Requested-With: e2e' 'https://$domain:$port/api/admin/overview'" > "$LOG/overview.json" \
  || fail "the admin overview did not answer"
grep -q '"services"' "$LOG/overview.json" || fail "the admin overview has no services: $(head -c 300 "$LOG/overview.json")"
# Every service the overview names is well (Loki takes a while on a from-zero start: two minutes at most).
not_ok() { python3 -c "import json; s=json.load(open('$LOG/overview.json'))['services']; print(', '.join(f\"{x['name']} ({x['detail']})\" for x in s if not x['ok']))"; }
for _ in $(seq 24); do
  [[ -z "$(not_ok)" ]] && break
  sleep 5
  H sh -c "curl -sk --resolve '$domain:$port:127.0.0.1' -b /tmp/e2e.cookies -H 'X-Requested-With: e2e' 'https://$domain:$port/api/admin/overview'" > "$LOG/overview.json" || true
done
[[ -z "$(not_ok)" ]] || fail "the admin overview says not ok: $(not_ok)"
say "  the admin signs in; the overview: every one of $(python3 -c "import json; print(len(json.load(open('$LOG/overview.json'))['services']))") services ok"
if [[ -n "$MODEL" ]]; then
  # The functional test, on the test host (no python there: in the stack's own python image, with the host's
  # docker for its LiteLLM questions and the names --hosts wrote).
  say "==> the functional test (deploy/scripts/functional-test.py, on the test host)"
  H docker image inspect python:3.13-slim >/dev/null 2>&1 || fail "python:3.13-slim is not on the test host"
  t0=$SECONDS
  dockerbin="$(H sh -c 'command -v docker')" || fail "no docker command on the test host"
  if H docker run --rm --network host -v /etc/hosts:/etc/hosts:ro -v /srv/arena/deploy:/srv/arena/deploy:ro \
      -v /var/run/docker.sock:/var/run/docker.sock -v "$dockerbin:/usr/local/bin/docker:ro" \
      python:3.13-slim python3 -I /srv/arena/deploy/scripts/functional-test.py > "$LOG/functional.log" 2>&1; then
    say "  functional: $(grep -c 'PASS' "$LOG/functional.log") checks passed, none failed, in $(( SECONDS - t0 )) s (log: $LOG/functional.log)"
  else
    # A test that stopped on an error has no FAIL line: the run still ends with its E2E FAIL.
    { grep -a 'FAIL' "$LOG/functional.log" || true; } | sed 's/^/    /' | tee -a "$LOG/run.log"
    tail -n 5 "$LOG/functional.log" | sed 's/^/    /' | tee -a "$LOG/run.log"
    fail "the functional test failed (above; $LOG/functional.log)"
  fi
fi
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
