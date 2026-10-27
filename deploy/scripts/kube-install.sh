#!/usr/bin/env bash
# Argus Arena on Kubernetes, with Helm (deploy/helm/argus-arena): what the cluster needs, each checked before
# anything is changed and each said with how to put it right; then the chart installed or upgraded, its secrets
# made in the cluster (never printed, never in a values file), and the stack checked as it answers. The offline
# installer runs it for --kubernetes; it runs on its own too, from a bundle or from the repository.
#
#   installer.sh COMMAND --kubernetes [options]      or      deploy/scripts/kube-install.sh COMMAND [options]
#
# Commands:
#   requirements  what the cluster has and lacks: kubectl and helm, the cluster and the rights in the namespace,
#                 storage (a default class; one that shares volumes between nodes for several app replicas), an
#                 ingress controller, the TLS (a Secret, or cert-manager's issuer), GPUs (NVIDIA's device plugin or
#                 GPU operator), the nodes' room (4 CPUs and 12 GiB; 8 and 24 with the GPU servers), and the
#                 metrics server for --autoscale
#   install       the requirements, then the namespace, the secrets, the images pushed (--registry), the values
#                 written (DIR/RELEASE-values.yaml: no secret in it), helm upgrade --install --atomic --wait, and
#                 the app answering
#   upgrade       an installed release to this chart: its secrets and its values kept (the domain, TLS, storage,
#                 database: helm --reset-then-reuse-values), the images pushed again (--registry); --atomic rolls a
#                 failed upgrade back
#   status        the release, its pods and the version the app says
#   remove        helm uninstall; --purge also the volumes (PersistentVolumeClaims) and the secrets, after the
#                 word PURGE (unattended: --confirm PURGE)
#
# Options:
#   --namespace NS            (default arena)        --release NAME   (default arena)
#   --context NAME            kubectl's context      --chart DIR      (default: the chart beside this script)
#   --domain NAME  --admin-email ADDRESS             (install; DOMAIN, gateway.DOMAIN and argus.DOMAIN)
#   --tls-secret NAME         a kubernetes.io/tls Secret in the namespace for the three names, or
#   --cluster-issuer NAME     cert-manager's ClusterIssuer, which makes it
#   --database in-cluster|external   (default in-cluster) with, external: --db-host HOST [--db-port 5432]
#                             [--db-user arena] [--db-ca-secret NAME] --db-password-file FILE (Postgres 16+)
#   --storage-class NAME      volumes (default: the cluster's default class)
#   --shared-storage-class NAME   the model library and what several replicas share: ReadWriteMany (NFS, CephFS,
#                             EFS, Azure Files, Filestore...); needed for more than one app replica on several nodes
#   --gpu yes|no|auto         the GPU servers (chat engine, pictures, video); auto: when a node has GPUs
#   --replicas N              the app's and the web's (default 2); the gateway's too with --valkey
#   --valkey                  the gateway's replicas share their counts (rate limits)
#   --autoscale               HorizontalPodAutoscalers for the app, the web and the gateway (the metrics server)
#   --strict-spread           never two replicas on one node
#   --registry HOST/PATH      air-gapped: the bundle's images loaded and pushed there, and pulled from it
#   --pull-secret NAME        the Secret the cluster pulls from that registry with
#   --gitlab-url URL --gitlab-token-file FILE   Argus's GitLab and its read-only token (read from the file)
#   --values FILE             more values of your own, last (they win)
#   --dir DIR                 where the values and logs are kept (default ~/.arena-kube)
#   --timeout MIN             how long helm waits (default 20)
#   --yes                     no questions        --dry-run   the plan: nothing is changed
#
# Exit codes: 0 done; 1 failed (the log says what); 2 a wrong command or option; 3 refused: a requirement not
# met, or no confirmation.
set -uo pipefail
umask 077

SELF="$(readlink -f "${BASH_SOURCE[0]}")"
HERE="$(dirname "$SELF")"
KUBE_MIN=1.27 HELM_MIN=3.14

CMD="${1:-}"
[[ $# -gt 0 ]] && shift
say() { printf '%s\n' "$*"; }
step() { printf '==> %s\n' "$*"; }
ok() { printf '  ok     %s\n' "$*"; }
bad() { printf '  NEEDS  %s\n' "$*"; }
note() { printf '  note   %s\n' "$*"; }
die() { printf 'kube-install: %s\n' "$*" >&2; exit 1; }
refuse() { printf 'kube-install: %s\n' "$*" >&2; exit 3; }
usage_error() { printf 'kube-install: %s (--help says how)\n' "$*" >&2; exit 2; }
usage() { awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "$SELF"; exit 0; }
gen_secret() { openssl rand -hex 32 2>/dev/null || od -An -N32 -tx1 /dev/urandom | tr -d ' \n'; }
# vercmp A B: 0 when A >= B (major.minor)
at_least() { [[ "$(printf '%s\n%s\n' "$2" "$1" | sort -V | head -n1)" == "$2" ]]; }

case "$CMD" in
  requirements|install|upgrade|status|remove) ;;
  help|-h|--help) usage ;;
  "") usage_error "say what to do: requirements, install, upgrade, status or remove" ;;
  *) usage_error "unknown command: $CMD" ;;
esac

NS=arena RELEASE=arena CONTEXT="" CHART="" DOMAIN="" EMAIL="" TLS_SECRET="" ISSUER="" DATABASE=in-cluster
DB_HOST="" DB_PORT=5432 DB_USER=arena DB_CA="" DB_PASSWORD_FILE="" STORAGE="" SHARED="" GPU=auto REPLICAS=2
VALKEY=0 AUTOSCALE=0 STRICT=0 REGISTRY="" PULL_SECRET="" GITLAB_URL="" GITLAB_TOKEN_FILE="" USER_VALUES=""
DIR="${HOME:-/root}/.arena-kube" TIMEOUT=20 YES=0 DRY=0 PURGE=0 CONFIRM=""
value() { [[ $2 -gt 1 ]] || usage_error "$1 needs a value"; }
while [[ $# -gt 0 ]]; do
  case "$1" in
    --kubernetes) ;;
    --namespace) value "$1" $#; NS="$2"; shift ;;
    --release) value "$1" $#; RELEASE="$2"; shift ;;
    --context) value "$1" $#; CONTEXT="$2"; shift ;;
    --chart) value "$1" $#; CHART="$2"; shift ;;
    --domain) value "$1" $#; DOMAIN="$2"; shift ;;
    --admin-email) value "$1" $#; EMAIL="$2"; shift ;;
    --tls-secret) value "$1" $#; TLS_SECRET="$2"; shift ;;
    --cluster-issuer) value "$1" $#; ISSUER="$2"; shift ;;
    --database) value "$1" $#; DATABASE="$2"; shift; [[ $DATABASE == in-cluster || $DATABASE == external ]] || usage_error "--database is in-cluster or external" ;;
    --db-host) value "$1" $#; DB_HOST="$2"; shift ;;
    --db-port) value "$1" $#; DB_PORT="$2"; shift; [[ "$DB_PORT" =~ ^[0-9]+$ ]] || usage_error "--db-port is a number" ;;
    --db-user) value "$1" $#; DB_USER="$2"; shift ;;
    --db-ca-secret) value "$1" $#; DB_CA="$2"; shift ;;
    --db-password-file) value "$1" $#; DB_PASSWORD_FILE="$2"; shift; [[ -r "$DB_PASSWORD_FILE" ]] || usage_error "cannot read $DB_PASSWORD_FILE" ;;
    --storage-class) value "$1" $#; STORAGE="$2"; shift ;;
    --shared-storage-class) value "$1" $#; SHARED="$2"; shift ;;
    --gpu) value "$1" $#; GPU="$2"; shift; [[ $GPU == yes || $GPU == no || $GPU == auto ]] || usage_error "--gpu is yes, no or auto" ;;
    --replicas) value "$1" $#; REPLICAS="$2"; shift; [[ "$REPLICAS" =~ ^[1-9][0-9]?$ ]] || usage_error "--replicas is 1 to 99" ;;
    --valkey) VALKEY=1 ;;
    --autoscale) AUTOSCALE=1 ;;
    --strict-spread) STRICT=1 ;;
    --registry) value "$1" $#; REGISTRY="${2%/}"; shift ;;
    --pull-secret) value "$1" $#; PULL_SECRET="$2"; shift ;;
    --gitlab-url) value "$1" $#; GITLAB_URL="$2"; shift ;;
    --gitlab-token-file) value "$1" $#; GITLAB_TOKEN_FILE="$2"; shift; [[ -r "$GITLAB_TOKEN_FILE" ]] || usage_error "cannot read $GITLAB_TOKEN_FILE" ;;
    --values) value "$1" $#; USER_VALUES="$2"; shift; [[ -r "$USER_VALUES" ]] || usage_error "cannot read $USER_VALUES" ;;
    --dir) value "$1" $#; DIR="$2"; shift ;;
    --timeout) value "$1" $#; TIMEOUT="$2"; shift; [[ "$TIMEOUT" =~ ^[1-9][0-9]*$ ]] || usage_error "--timeout is minutes" ;;
    --yes|-y) YES=1 ;;
    --dry-run) DRY=1 ;;
    --purge) PURGE=1 ;;
    --confirm) value "$1" $#; CONFIRM="$2"; shift ;;
    -h|--help) usage ;;
    *) usage_error "unknown option: $1" ;;
  esac
  shift
done
[[ "$NS" =~ ^[a-z0-9]([-a-z0-9]*[a-z0-9])?$ ]] || usage_error "--namespace: lowercase letters, digits and -"
[[ "$RELEASE" =~ ^[a-z0-9]([-a-z0-9]*[a-z0-9])?$ ]] || usage_error "--release: lowercase letters, digits and -"
[[ -n "$TLS_SECRET" && -n "$ISSUER" ]] && usage_error "--tls-secret or --cluster-issuer, not both"
[[ $DATABASE == external && ( -z "$DB_HOST" || -z "$DB_PASSWORD_FILE" ) && $CMD == install ]] && usage_error "--database external needs --db-host and --db-password-file"
if [[ -z "$CHART" ]]; then
  for c in "$HERE/../helm/argus-arena" "$HERE/deploy/helm/argus-arena"; do [[ -f "$c/Chart.yaml" ]] && { CHART="$(cd "$c" && pwd)"; break; }; done
fi
[[ -f "$CHART/Chart.yaml" ]] || usage_error "no chart: --chart DIR (deploy/helm/argus-arena)"
# The bundle's images (images/IMAGES: ref, file...), when this runs from one.
BUNDLE=""
for b in "$HERE/../.." "$HERE/.."; do [[ -f "$b/images/IMAGES" ]] && { BUNDLE="$(cd "$b" && pwd)"; break; }; done

K() { kubectl ${CONTEXT:+--context "$CONTEXT"} "$@"; }
H() { helm ${CONTEXT:+--kube-context "$CONTEXT"} "$@"; }
KN() { K -n "$NS" "$@"; }
SECRET="$RELEASE-secrets"
VALUES="$DIR/$RELEASE-values.yaml"

# ================================================================ requirements
FAIL=0 HAS_GPU=0 NODES=0
need() { bad "$*"; FAIL=$((FAIL + 1)); }
json() { python3 -c "import json, sys; d = json.load(sys.stdin); $1" 2>/dev/null; }

check_requirements() {
  step "The tools"
  if ! command -v kubectl >/dev/null; then need "kubectl: install it (https://kubernetes.io/docs/tasks/tools/), $KUBE_MIN or newer"
  else
    local kv; kv="$(kubectl version --client -o json 2>/dev/null | json 'print(d["clientVersion"]["major"] + "." + d["clientVersion"]["minor"].rstrip("+"))')"
    if [[ -n "$kv" ]] && at_least "$kv" "$KUBE_MIN"; then ok "kubectl $kv"; else need "kubectl ${kv:-?} is older than $KUBE_MIN: install a newer one"; fi
  fi
  if ! command -v helm >/dev/null; then need "helm: install it (https://helm.sh/docs/intro/install/), $HELM_MIN or newer"
  else
    local hv; hv="$(helm version --template '{{.Version}}' 2>/dev/null | sed 's/^v//')"
    if [[ -n "$hv" ]] && at_least "$hv" "$HELM_MIN"; then ok "helm $hv"; else need "helm ${hv:-?} is older than $HELM_MIN: install a newer one"; fi
  fi
  command -v python3 >/dev/null || need "python3: this script reads the cluster's answers with it"
  [[ $FAIL -eq 0 ]] || return

  step "The cluster"
  local sv; sv="$(K version -o json 2>/dev/null | json 'print(d["serverVersion"]["major"] + "." + d["serverVersion"]["minor"].rstrip("+"))')"
  if [[ -z "$sv" ]]; then need "the cluster does not answer: check kubectl's context (kubectl config current-context${CONTEXT:+, --context $CONTEXT}) and that you are signed in"; return; fi
  if at_least "$sv" "$KUBE_MIN"; then ok "Kubernetes $sv"; else need "Kubernetes $sv is older than $KUBE_MIN: upgrade the cluster"; fi
  local can r
  if K get namespace "$NS" >/dev/null 2>&1; then ok "namespace $NS is there"
  elif [[ "$(K auth can-i create namespaces 2>/dev/null)" == yes ]]; then ok "namespace $NS will be made"
  else need "namespace $NS: there is none and you may not make one: kubectl create namespace $NS (a cluster admin)"; fi
  can=""
  for r in deployments statefulsets services secrets configmaps persistentvolumeclaims ingresses networkpolicies poddisruptionbudgets daemonsets; do
    [[ "$(K auth can-i create "$r" -n "$NS" 2>/dev/null)" == yes ]] || can="$can $r"
  done
  if [[ -z "$can" ]]; then ok "you may make what the chart makes in $NS"; else need "you may not make$can in $NS: a cluster admin gives your account a role there (edit, or admin)"; fi

  step "Storage"
  local classes default
  classes="$(K get storageclass -o json 2>/dev/null | json 'print("\n".join(i["metadata"]["name"] + ("\tdefault" if i["metadata"].get("annotations", {}).get("storageclass.kubernetes.io/is-default-class") == "true" else "") for i in d["items"]))')"
  default="$(printf '%s\n' "$classes" | awk -F'\t' '$2 == "default" { print $1; exit }')"
  if [[ -n "$STORAGE" ]]; then
    if printf '%s\n' "$classes" | cut -f1 | grep -qxF "$STORAGE"; then ok "storage class $STORAGE"; else need "storage class $STORAGE is not in the cluster (kubectl get storageclass)"; fi
  elif [[ -n "$default" ]]; then ok "the default storage class: $default"
  else need "a storage class: the cluster has no default; name one with --storage-class (kubectl get storageclass), or install a provisioner"; fi
  NODES="$(K get nodes -o json 2>/dev/null | json 'print(len(d["items"]))')"
  NODES="${NODES:-0}"
  if [[ $CMD == upgrade ]]; then note "the release's storage stays as it was installed"
  elif [[ -n "$SHARED" ]]; then
    if printf '%s\n' "$classes" | cut -f1 | grep -qxF "$SHARED"; then ok "shared storage class $SHARED (it must give ReadWriteMany volumes)"; else need "storage class $SHARED is not in the cluster"; fi
  elif [[ $REPLICAS -gt 1 && $NODES -gt 1 ]]; then
    need "a storage class that shares volumes between nodes (ReadWriteMany: NFS, CephFS, EFS, Azure Files, Filestore) for $REPLICAS app replicas on $NODES nodes: --shared-storage-class NAME, or --replicas 1"
  else note "one node, or one replica: the model library needs no shared storage"; fi

  step "The way in"
  local ingress; ingress="$(K get ingressclass -o json 2>/dev/null | json 'print(" ".join(i["metadata"]["name"] for i in d["items"]))')"
  if [[ -n "$ingress" ]]; then ok "ingress controller: $ingress"; else need "an ingress controller (ingress-nginx or Traefik): none has an IngressClass"; fi
  if [[ -n "$ISSUER" ]]; then
    if K get clusterissuer "$ISSUER" >/dev/null 2>&1; then ok "cert-manager's ClusterIssuer $ISSUER"; else need "ClusterIssuer $ISSUER: not there (is cert-manager installed? kubectl get clusterissuer)"; fi
  elif [[ -n "$TLS_SECRET" ]]; then
    local type; type="$(KN get secret "$TLS_SECRET" -o jsonpath='{.type}' 2>/dev/null)"
    if [[ "$type" == kubernetes.io/tls ]]; then ok "TLS Secret $TLS_SECRET"; else need "TLS Secret $TLS_SECRET in $NS: kubectl create secret tls $TLS_SECRET -n $NS --cert=FILE --key=FILE (for DOMAIN, gateway.DOMAIN and argus.DOMAIN)"; fi
  elif [[ $CMD == upgrade ]]; then note "the release's TLS stays as it was installed"
  else
    need "the TLS: --tls-secret NAME (a kubernetes.io/tls Secret) or --cluster-issuer NAME (cert-manager)"
  fi

  step "GPUs"
  local gpus; gpus="$(K get nodes -o json 2>/dev/null | json 'print(sum(int(i["status"].get("allocatable", {}).get("nvidia.com/gpu", "0")) for i in d["items"]))')"
  gpus="${gpus:-0}"
  if [[ $gpus -gt 0 ]]; then HAS_GPU=1; ok "$gpus GPU(s) the cluster can give (nvidia.com/gpu)"
  elif [[ $GPU == yes ]]; then need "GPUs: no node gives nvidia.com/gpu; install NVIDIA's GPU operator (or its device plugin) on the GPU nodes"
  else note "no GPUs: the chat engine, pictures and video are left out (models from other servers, through the gateway)"; fi

  step "Room"
  local room; room="$(K get nodes -o json 2>/dev/null | json '
def cpu(v): return float(v[:-1]) / 1000 if v.endswith("m") else float(v)
def mem(v):
    units = {"Ki": 2**10, "Mi": 2**20, "Gi": 2**30, "Ti": 2**40, "K": 1e3, "M": 1e6, "G": 1e9}
    for u, f in units.items():
        if v.endswith(u): return float(v[:-len(u)]) * f / 2**30
    return float(v) / 2**30
a = [i["status"].get("allocatable", {}) for i in d["items"]]
print("%d %d" % (sum(cpu(x.get("cpu", "0")) for x in a), sum(mem(x.get("memory", "0")) for x in a)))')"
  # The GPU servers' pods want more of the nodes than the rest of the stack.
  local cpus mem least_cpu=4 least_mem=12; read -r cpus mem <<< "${room:-0 0}"
  if [[ $HAS_GPU -eq 1 || $GPU == yes ]] && [[ $GPU != no ]]; then least_cpu=8 least_mem=24; fi
  if [[ ${cpus:-0} -ge $least_cpu && ${mem:-0} -ge $least_mem ]]; then ok "the nodes have $cpus CPUs and $mem GiB to give"
  else need "room: the nodes give $cpus CPUs and $mem GiB; the stack needs $least_cpu CPUs and $least_mem GiB at least (more nodes, or bigger ones)"; fi

  if [[ $AUTOSCALE -eq 1 ]]; then
    if K get apiservice v1beta1.metrics.k8s.io >/dev/null 2>&1; then ok "the metrics server (autoscaling reads it)"
    else need "the metrics server for --autoscale: install metrics-server (https://github.com/kubernetes-sigs/metrics-server)"; fi
  fi
  if [[ -n "$REGISTRY" ]]; then
    [[ -n "$BUNDLE" ]] || need "--registry pushes the bundle's images: run this from an unpacked bundle"
    command -v docker >/dev/null || command -v podman >/dev/null || need "docker or podman: to load and push the bundle's images"
  fi
  if [[ $DATABASE == external ]]; then
    note "external Postgres $DB_HOST:$DB_PORT as $DB_USER (16 or newer, reachable from the cluster; the app makes its database)"
    [[ -z "$DB_CA" ]] || KN get secret "$DB_CA" >/dev/null 2>&1 || need "the database's CA Secret $DB_CA in $NS (ca.crt): kubectl create secret generic $DB_CA -n $NS --from-file=ca.crt=FILE"
  fi
}

# ================================================================ secrets, images, values
# The secrets as a manifest on stdin: never in a command line, a file or the log.
make_secrets() {
  if KN get secret "$SECRET" >/dev/null 2>&1; then ok "secrets kept: $SECRET (APP_KEY never changes)"; return; fi
  if [[ $DRY -eq 1 ]]; then note "would make the Secret $SECRET: ADMIN_PASSWORD, DB_PASSWORD, APP_KEY, ENGINE_KEY, ARGUS_KEY, WORKSPACES_KEY, GATEWAY_KEY"; return; fi
  local db gitlab=""
  db="$(gen_secret)"
  [[ $DATABASE == external ]] && db="$(head -n1 "$DB_PASSWORD_FILE")"
  [[ -n "$GITLAB_TOKEN_FILE" ]] && gitlab="$(head -n1 "$GITLAB_TOKEN_FILE")"
  {
    printf 'apiVersion: v1\nkind: Secret\nmetadata:\n  name: %s\n  labels: { app.kubernetes.io/part-of: argus-arena }\ntype: Opaque\nstringData:\n' "$SECRET"
    printf '  ADMIN_PASSWORD: "%s"\n  DB_PASSWORD: "%s"\n  APP_KEY: "%s"\n  ENGINE_KEY: "%s"\n  ARGUS_KEY: "%s"\n  WORKSPACES_KEY: "%s"\n  GATEWAY_KEY: "sk-%s"\n' \
      "$(gen_secret | cut -c1-24)" "$db" "$(gen_secret)" "$(gen_secret)" "$(gen_secret)" "$(gen_secret)" "$(gen_secret)"
    [[ -z "$gitlab" ]] || printf '  GITLAB_TOKEN: "%s"\n' "$gitlab"
  } | KN apply -f - >/dev/null || die "could not make the Secret $SECRET"
  ok "secrets made in the cluster: $SECRET"
  say "  The admin's password (user admin) is in the Secret, never printed here; read it with:"
  say "    kubectl -n $NS get secret $SECRET -o jsonpath='{.data.ADMIN_PASSWORD}' | base64 -d"
}

push_images() {
  [[ -n "$REGISTRY" ]] || return 0
  local engine=docker ref file
  command -v docker >/dev/null || engine=podman
  while IFS=$'\t' read -r ref file _; do
    [[ -n "$ref" ]] || continue
    if [[ $DRY -eq 1 ]]; then note "would push $ref to $REGISTRY/${ref##*/}"; continue; fi
    "$engine" image inspect "$ref" >/dev/null 2>&1 || gzip -dc "$BUNDLE/images/$file" | "$engine" load >/dev/null || die "could not load $file"
    "$engine" tag "$ref" "$REGISTRY/${ref##*/}" && "$engine" push "$REGISTRY/${ref##*/}" >/dev/null || die "could not push $ref to $REGISTRY"
    ok "pushed $REGISTRY/${ref##*/}"
  done < "$BUNDLE/images/IMAGES"
}

# What the chart is told, but secrets: written to VALUES and given to helm with -f.
write_values() {
  local tls=arena-tls gpu=$HAS_GPU
  [[ $GPU == yes ]] && gpu=1
  [[ $GPU == no ]] && gpu=0
  [[ -n "$TLS_SECRET" ]] && tls="$TLS_SECRET"
  {
    printf '# Written by kube-install.sh for release %s in %s. No secret is here: they are in the Secret %s.\n' "$RELEASE" "$NS" "$SECRET"
    printf 'domain: "%s"\nadminEmail: "%s"\n' "$DOMAIN" "$EMAIL"
    printf 'secrets: { existingSecret: "%s" }\n' "$SECRET"
    printf 'ingress:\n  tlsSecret: "%s"\n' "$tls"
    [[ -z "$ISSUER" ]] || printf '  annotations: { cert-manager.io/cluster-issuer: "%s" }\n' "$ISSUER"
    printf 'persistence: { storageClass: "%s", sharedStorageClass: "%s" }\n' "$STORAGE" "$SHARED"
    printf 'app: { replicas: %s }\nweb: { replicas: %s }\n' "$REPLICAS" "$REPLICAS"
    if [[ $VALKEY -eq 1 ]]; then printf 'valkey: { enabled: true }\nlitellm: { replicas: %s }\n' "$REPLICAS"; fi
    printf 'availability: { strictSpread: %s, autoscale: { enabled: %s } }\n' "$( ((STRICT)) && echo true || echo false)" "$( ((AUTOSCALE)) && echo true || echo false)"
    if [[ $gpu -eq 0 ]]; then
      printf 'llamacpp: { enabled: false }\nimagegen: { enabled: false }\nvideogen: { enabled: false }\nvllm: { enabled: false }\nstudio: { enabled: false }\n'
    fi
    if [[ $DATABASE == external ]]; then
      printf 'postgresql: { enabled: false }\nexternalDatabase: { host: "%s", port: %s, username: "%s", caSecret: "%s" }\n' "$DB_HOST" "$DB_PORT" "$DB_USER" "$DB_CA"
    fi
    [[ -z "$GITLAB_URL" ]] || printf 'gitlab: { url: "%s" }\n' "$GITLAB_URL"
    [[ -z "$REGISTRY" ]] || image_values
  } > "$1"
}

# Each of the bundle's images as the chart names it (images.KEY, matched by its repository's last part), from REGISTRY.
image_values() {
  printf 'images:\n'
  [[ -z "$PULL_SECRET" ]] || printf '  pullSecrets: ["%s"]\n' "$PULL_SECRET"
  python3 - "$CHART/values.yaml" "$BUNDLE/images/IMAGES" "$REGISTRY" <<'PY'
import re, sys
values, images, registry = sys.argv[1:4]
keys = {}
inside = False
for line in open(values, encoding="utf-8"):
    if re.match(r"^images:", line):
        inside = True
        continue
    if inside and re.match(r"^\S", line):
        break
    m = re.match(r"^  ([A-Za-z0-9]+): \{ repository: ([^,}]+)", line)
    if inside and m:
        keys[m.group(2).strip().strip('"').rsplit("/", 1)[-1]] = m.group(1)
for row in open(images, encoding="utf-8"):
    ref = row.split("\t")[0].strip()
    if not ref:
        continue
    base = ref.rsplit("/", 1)[-1]
    name, tag = base.split(":", 1) if ":" in base else (base, "latest")
    if name in keys:
        print(f'  {keys[name]}: {{ repository: "{registry}/{name}", tag: "{tag}" }}')
PY
}

app_answers() {   # the app's /api/info through the API server's proxy: no port opened
  local i info
  for i in $(seq 1 60); do
    info="$(K get --raw "/api/v1/namespaces/$NS/services/app:8080/proxy/api/info" 2>/dev/null)" && [[ "$info" == *version* ]] && { printf '%s' "$info" | json 'print(d["version"])'; return 0; }
    [[ $DRY -eq 1 ]] && return 1
    sleep 5
  done
  return 1
}

# ================================================================ commands
cmd_requirements() {
  check_requirements
  if [[ $FAIL -gt 0 ]]; then say ""; refuse "$FAIL requirement(s) not met: put them right as said above, then run again"; fi
  say ""; say "The cluster has what Argus Arena needs."
}

cmd_install() {
  if [[ $CMD == install ]]; then
    [[ -n "$DOMAIN" ]] || usage_error "install needs --domain (people open https://DOMAIN)"
    [[ -n "$EMAIL" ]] || EMAIL="admin@$DOMAIN"
  fi
  local installed=0
  H status "$RELEASE" -n "$NS" >/dev/null 2>&1 && installed=1
  [[ $CMD == install && $installed -eq 1 ]] && refuse "release $RELEASE is installed in $NS already: upgrade it (kube-install.sh upgrade)"
  [[ $CMD == upgrade && $installed -eq 0 ]] && refuse "no release $RELEASE in $NS to upgrade: install it"
  # An upgrade keeps the release's values (helm --reset-then-reuse-values: the new chart's defaults, then what was
  # set): the domain, the TLS, the storage and the database stay as installed.
  [[ $CMD == upgrade ]] && DOMAIN="$(H get values "$RELEASE" -n "$NS" -o json 2>/dev/null | json 'print(d.get("domain", ""))')"
  check_requirements
  [[ $FAIL -eq 0 ]] || { say ""; refuse "$FAIL requirement(s) not met: put them right as said above, then run again"; }

  step "The plan"
  say "  release $RELEASE in namespace $NS, https://$DOMAIN ($([[ $DATABASE == external ]] && echo "Postgres at $DB_HOST" || echo "Postgres in the cluster"))"
  say "  $REPLICAS replica(s) of the app and the web$( ((AUTOSCALE)) && echo ", grown with their load")$( ((HAS_GPU)) || [[ $GPU == yes ]] && echo "; GPU servers on" || echo "; no GPU servers")"
  if [[ $DRY -eq 0 && $YES -eq 0 && -t 0 ]]; then
    local a; read -r -p "Go on? [y/N] " a; [[ "$a" == [yY]* ]] || refuse "not confirmed"
  fi

  mkdir -p "$DIR" || die "cannot make $DIR"
  step "The namespace and the secrets"
  if ! K get namespace "$NS" >/dev/null 2>&1; then
    if [[ $DRY -eq 1 ]]; then note "would make namespace $NS"; else K create namespace "$NS" >/dev/null || die "could not make namespace $NS"; ok "namespace $NS made"; fi
  fi
  make_secrets
  if [[ -n "$REGISTRY" ]]; then step "The images, to $REGISTRY"; push_images; fi

  step "The chart"
  if [[ $CMD == upgrade ]]; then
    local images="$DIR/$RELEASE-images.yaml" args=(--reset-then-reuse-values)
    if [[ -n "$REGISTRY" ]]; then image_values > "$images"; args+=(-f "$images"); fi
    if [[ $DRY -eq 1 ]]; then note "would run: helm upgrade $RELEASE $CHART -n $NS ${args[*]} --atomic --wait --timeout ${TIMEOUT}m${USER_VALUES:+ -f $USER_VALUES}"; return 0; fi
    H upgrade "$RELEASE" "$CHART" -n "$NS" "${args[@]}" --atomic --wait --timeout "${TIMEOUT}m" ${USER_VALUES:+-f "$USER_VALUES"} \
      || die "helm did not finish: the release was rolled back to what ran before; kubectl -n $NS get events --sort-by=.lastTimestamp says why"
  else
    write_values "$VALUES.new"
    if [[ $DRY -eq 1 ]]; then
      note "would write $VALUES:"; sed 's/^/    /' "$VALUES.new"; rm -f "$VALUES.new"
      note "would run: helm upgrade --install $RELEASE $CHART -n $NS --atomic --wait --timeout ${TIMEOUT}m -f $VALUES${USER_VALUES:+ -f $USER_VALUES}"
      return 0
    fi
    mv "$VALUES.new" "$VALUES"
    ok "values: $VALUES"
    H upgrade --install "$RELEASE" "$CHART" -n "$NS" --atomic --wait --timeout "${TIMEOUT}m" -f "$VALUES" ${USER_VALUES:+-f "$USER_VALUES"} \
      || die "helm did not finish: nothing is left running (--atomic); kubectl -n $NS get events --sort-by=.lastTimestamp says why"
  fi
  ok "helm: $RELEASE is deployed"

  step "The stack answers"
  local version
  if version="$(app_answers)"; then ok "the app answers: Argus Arena $version"
  else die "the app does not answer through its Service: kubectl -n $NS logs deploy/app"; fi
  say ""
  say "Argus Arena runs at https://$DOMAIN (the DNS names $DOMAIN, gateway.$DOMAIN and argus.$DOMAIN lead to the ingress)."
}

cmd_status() {
  H status "$RELEASE" -n "$NS" >/dev/null 2>&1 || refuse "no release $RELEASE in $NS"
  H status "$RELEASE" -n "$NS" 2>/dev/null | sed -n '1,8p'
  KN get deployments,statefulsets -o wide 2>/dev/null
  local version; DRY=1; version="$(app_answers)" && say "The app answers: Argus Arena $version" || say "The app does not answer now."
}

cmd_remove() {
  H status "$RELEASE" -n "$NS" >/dev/null 2>&1 || note "no release $RELEASE in $NS"
  if [[ $PURGE -eq 1 ]]; then
    local word="$CONFIRM"
    if [[ -z "$word" && -t 0 && $DRY -eq 0 ]]; then read -r -p "This removes the data of $RELEASE in $NS for good (its volumes and secrets). Type PURGE: " word; fi
    [[ "$word" == PURGE || $DRY -eq 1 ]] || refuse "--purge removes the data: confirm with the word PURGE (--confirm PURGE)"
  fi
  if [[ $DRY -eq 1 ]]; then note "would run helm uninstall $RELEASE -n $NS$( ((PURGE)) && echo ", then remove its PersistentVolumeClaims and the Secret $SECRET")"; return 0; fi
  H uninstall "$RELEASE" -n "$NS" --wait >/dev/null 2>&1 && ok "helm uninstall $RELEASE"
  if [[ $PURGE -eq 1 ]]; then
    KN delete pvc -l app.kubernetes.io/part-of=argus-arena --wait=false >/dev/null 2>&1
    KN delete secret "$SECRET" --ignore-not-found >/dev/null 2>&1
    ok "its volumes and secrets removed"
  else
    say "The data stays (its PersistentVolumeClaims and the Secret $SECRET): install again to use it, or remove --purge."
  fi
}

case "$CMD" in
  requirements) cmd_requirements ;;
  install|upgrade) cmd_install ;;
  status) cmd_status ;;
  remove) cmd_remove ;;
esac
