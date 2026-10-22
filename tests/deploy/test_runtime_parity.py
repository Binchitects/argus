"""
The three runtimes run one stack: Docker Compose (deploy/docker-compose.yml, with scale.yml), rootless Podman
(podman.yml on top) and Kubernetes (the Helm chart, deploy/helm/argus-arena). A service, an environment variable,
a mounted file or script, a secret or a route the app is reached by that is in one runtime and not the others fails
here, unless ALLOWED names it with the reason (docs/deployment.md, "Runtimes", says the same to people).

Compose's side is what `docker compose config` resolves (or `podman compose config`), from .env.example with
placeholders: never the host's .env. It only reads; without either command those tests are skipped. The chart's side
is `helm template` where helm and PyYAML are installed, else the templates read as text: every container, the
variables it is given and the paths it mounts, whatever values turn them on.
"""
from __future__ import annotations

import importlib.util
import json
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from support import REPO

DEPLOY = REPO / "deploy"
CHART = DEPLOY / "helm" / "argus-arena"

# What differs on purpose. Each key is (what, service, name); "*" as the name is the whole service.
ALLOWED: dict[tuple[str, str, str], str] = {
    # Services compose runs that the chart leaves to the cluster.
    ("service", "traefik", "*"): "Kubernetes: the cluster's ingress controller and the Ingress (TLS from a Secret)",
    ("service", "promtail", "*"): "Kubernetes: the cluster's log agent ships the pods' logs to Loki",
    ("service", "gpu-exporter", "*"): "Kubernetes: GPU telemetry is the cluster's (DCGM exporter); GPU placement work",
    ("service", "power-limits", "*"): "privileged, writes the host's /sys: compose on Docker only",
    # Environment.
    ("env", "app", "Storage__BackupsKept"): "backups are backup.sh's (compose); a cluster backs up its own volumes",
    ("env", "app", "Stack__Acme"): "Traefik's Let's Encrypt (compose); the Ingress takes a TLS Secret",
    ("env", "app", "Engine__Enabled"): "Helm can leave the engine out (llamacpp.enabled); compose always runs it",
    ("env", "litellm", "REDIS_HOST"): "the gateway's shared counts: ha.yml on compose, valkey.enabled on Helm",
    ("env", "litellm", "REDIS_PORT"): "as REDIS_HOST",
    ("env", "app", "Engine__LlamaCpp"): "Helm can run vLLM without llama.cpp (llamacpp.enabled false); compose always runs llama.cpp",
    ("env", "app", "Modules__imagegen"): "Helm says outright which modules run; compose's app looks the names up",
    ("env", "app", "Modules__videogen"): "as Modules__imagegen",
    ("env", "app", "Modules__audio"): "as Modules__imagegen",
    ("env", "app", "Modules__laya"): "as Modules__imagegen",
    ("env", "app", "Modules__studio"): "as Modules__imagegen",
    ("env", "app", "Database__RootCertificate"): "externalDatabase.caSecret; compose sets it in the override",
    ("env", "litellm", "DB_PASSWORD"): "Kubernetes builds DATABASE_URL from it ($(DB_PASSWORD)); compose interpolates",
    ("env", "postgres", "PGDATA"): "a volume's root is not empty on some storage (lost+found)",
    # Mounts.
    ("mount", "app", "/backups"): "backup.sh's backups, for Admin -> Storage: compose only",
    ("mount", "app", "/db-ca"): "externalDatabase.caSecret; compose mounts the CA in the override",
    ("mount", "llamacpp", "/dev/shm"): "compose's shm_size, as an emptyDir in memory",
    ("mount", "vllm", "/dev/shm"): "compose's shm_size, as an emptyDir in memory",
    ("mount", "imagegen", "/tmp"): "the chart gives every read-only pod a /tmp; compose's imagegen needs none",
    ("mount", "prometheus", "/tmp"): "the scrape tokens' files: an emptyDir in the chart, the container's own /tmp in compose",
}

PODMAN_ALLOWED: dict[tuple[str, str, str], str] = {
    ("service", "power-limits", "*"): "needs the host's root",
    ("mount", "promtail", "/var/lib/docker/containers"): "rootless Podman: its logs are read through its API",
}

# The chart's workloads; a compose service is the container of its name in one of them.
WORKLOAD_KINDS = ("Deployment", "StatefulSet", "DaemonSet")


def compose_command() -> list[str] | None:
    """docker compose, else podman compose: `config` only reads the files."""
    for engine in ("docker", "podman"):
        path = shutil.which(engine)
        if path is None:
            continue
        try:
            if subprocess.run([path, "compose", "version"], capture_output=True, timeout=60).returncode == 0:
                return [path, "compose"]
        except (OSError, subprocess.TimeoutExpired):
            continue
    return None


_configs: dict[tuple[str, ...], dict] = {}


def compose_config(*files: str) -> dict:
    """The stack as compose resolves it, with the Laya, vLLM, HA and 3D profiles on (every module), from .env.example's placeholders."""
    if files in _configs:
        return _configs[files]
    cmd = compose_command()
    if cmd is None:
        raise unittest.SkipTest("no docker compose or podman compose here")
    example = (DEPLOY / ".env.example").read_text()
    example = re.sub(r"^(ADMIN_PASSWORD|DB_PASSWORD|APP_KEY|ENGINE_KEY|ARGUS_KEY)=$", r"\1=parity", example, flags=re.M)
    example = re.sub(r"^GATEWAY_KEY=$", "GATEWAY_KEY=sk-parity", example, flags=re.M)
    example = re.sub(r"^DATA_DIR=$", "DATA_DIR=/srv/arena/data", example, flags=re.M)
    with tempfile.TemporaryDirectory(prefix="parity-") as tmp:
        env_file = Path(tmp) / "parity.env"
        env_file.write_text(example)
        env = {k: v for k, v in os.environ.items() if not k.startswith(("COMPOSE_", "DOCKER_HOST"))}
        env.update({"COMPOSE_PROFILES": "laya,vllm,ha,3d,studio", "PODMAN_COMPOSE_WARNING_LOGS": "false"})
        args = [*cmd, "--env-file", str(env_file)]
        for f in files:
            args += ["-f", str(DEPLOY / f)]
        r = subprocess.run(args + ["config", "--format", "json"], capture_output=True, text=True, env=env, timeout=120)
    if r.returncode != 0:
        # Its error names files and keys, never the values (placeholders anyway).
        raise AssertionError(f"compose config failed for {files}: {r.stderr.strip()[-2000:]}")
    _configs[files] = json.loads(r.stdout[r.stdout.index("{"):])
    return _configs[files]


def compose_services(config: dict) -> dict[str, dict[str, set[str]]]:
    """Each service: its environment's names and the paths it mounts (volumes, binds and tmpfs)."""
    out = {}
    for name, s in config["services"].items():
        env = s.get("environment") or {}
        mounts = {v["target"] for v in s.get("volumes") or []}
        mounts |= {t.split(":", 1)[0] for t in s.get("tmpfs") or []}
        binds = {v["target"]: v["source"] for v in s.get("volumes") or [] if v.get("type") == "bind"}
        out[name] = {"env": set(env), "mounts": mounts, "binds": binds}
    return out


def helm_rendered() -> dict[str, dict[str, set[str]]] | None:
    """`helm template` with every module on, where helm and PyYAML are both here; else None."""
    helm = shutil.which("helm")
    try:
        import yaml  # noqa: PLC0415
    except ImportError:
        return None
    if helm is None:
        return None
    on = ["laya.enabled=true", "studio.enabled=true", "blender.enabled=true", "videogen.enabled=true", "vllm.enabled=true", "valkey.enabled=true", "argus.packLibraryClaim=packs"]
    out: dict[str, dict[str, set[str]]] = {}
    # This chart's Postgres, and an external one with a CA of its own: together, every variable a pod can be given.
    # The output holds the Secret it made up: it is read here and never printed.
    for sets in (on, on + ["postgresql.enabled=false", "externalDatabase.host=db", "externalDatabase.caSecret=ca"]):
        r = subprocess.run([helm, "template", "arena", str(CHART), *[a for s in sets for a in ("--set", s)]],
                           capture_output=True, text=True, timeout=120)
        if r.returncode != 0:
            raise AssertionError(f"helm template failed: {r.stderr.strip()[-2000:]}")
        for doc in yaml.safe_load_all(r.stdout):
            if not doc or doc.get("kind") not in WORKLOAD_KINDS:
                continue
            for c in doc["spec"]["template"]["spec"]["containers"]:
                item = out.setdefault(c["name"], {"env": set(), "mounts": set()})
                item["env"] |= {e["name"] for e in c.get("env") or []}
                item["mounts"] |= {m["mountPath"] for m in c.get("volumeMounts") or []}
    return out


def helm_static() -> dict[str, dict[str, set[str]]]:
    """The chart's containers read from the templates as text: each container's variables and mount paths, with
    every if taken (a value can turn each on). A container named {{ .name }} is each name the template lists."""
    out: dict[str, dict[str, set[str]]] = {}
    for path in sorted((CHART / "templates").glob("*.yaml")):
        text = path.read_text()
        listed = re.findall(r'dict "name" "([\w-]+)"', text)
        for doc in re.split(r"^---\s*$", text, flags=re.M):
            if not re.search(rf"^kind: ({'|'.join(WORKLOAD_KINDS)})\s*$", doc, re.M):
                continue
            lines = doc.splitlines()
            starts = []
            for i, line in enumerate(lines):
                m = re.match(r"^(\s*)- name: (\S.*)$", line)
                nxt = next((x.strip() for x in lines[i + 1:] if x.strip()), "")
                if m and nxt.startswith("image:"):
                    starts.append((i, len(m.group(1)), m.group(2).strip()))
            for i, indent, name in starts:
                block = [lines[i]]
                for line in lines[i + 1:]:
                    stripped = line.strip()
                    if stripped and not stripped.startswith("{{") and len(line) - len(line.lstrip()) <= indent:
                        break
                    block.append(line)
                body = "\n".join(block)
                env = set(re.findall(r"\{ name: ([A-Za-z_]\w*), value", body))
                env |= set(re.findall(r'(?:secretEnv|optionalSecretEnv)" \(list \$ "([A-Za-z_]\w*)"', body))
                env |= set(re.findall(r"^\s*- name: ([A-Za-z_]\w*)\s*\n\s*value", body, re.M))
                mounts = {p for p in re.findall(r"\{ name: [\w-]+, mountPath: ([^,}\s]+)", body) if "{{" not in p}
                for n in (listed if name == "{{ .name }}" else [name]):
                    item = out.setdefault(n, {"env": set(), "mounts": set()})
                    item["env"] |= env
                    item["mounts"] |= mounts
    return out


def chart_copies() -> dict[str, str]:
    """check-chart.py's list: the chart's file for each of deploy/'s it carries."""
    spec = importlib.util.spec_from_file_location("check_chart", DEPLOY / "helm" / "check-chart.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.COPIES


def covered(path: str, others: set[str]) -> bool:
    """A path is the same mount as one of others, or a folder holding it, or a file in it (config/prometheus is a
    folder in compose and its files in the chart)."""
    return any(path == o or o.startswith(path.rstrip("/") + "/") or path.startswith(o.rstrip("/") + "/") for o in others)


class Diff:
    """Differences between two runtimes, less the allowed ones; the allowed ones never seen are stale."""

    def __init__(self, allowed: dict[tuple[str, str, str], str]):
        self.allowed, self.used, self.problems = allowed, set(), []

    def add(self, key: tuple[str, str, str], text: str):
        if key in self.allowed:
            self.used.add(key)
        else:
            self.problems.append(text)

    def stale(self, kinds: tuple[str, ...]) -> list[str]:
        return [f"{k} is allowed but no longer differs: take it out of the allow-list"
                for k in self.allowed if k[0] in kinds and k not in self.used]


class ComposeAndHelmTests(unittest.TestCase):
    """Compose (Docker) against the chart: the same services, variables and mounts, but ALLOWED."""

    @classmethod
    def setUpClass(cls):
        cls.compose = compose_services(compose_config("docker-compose.yml"))
        rendered = helm_rendered()
        cls.helm = rendered if rendered is not None else helm_static()
        cls.how = "helm template" if rendered is not None else "the templates as text"

    def report(self, d: Diff, kinds: tuple[str, ...]):
        problems = d.problems + d.stale(kinds)
        self.assertEqual(problems, [], f"compose against the chart ({self.how}); add it to every runtime, or to "
                                       "ALLOWED with the reason in docs/deployment.md, \"Runtimes\"")

    def test_The_same_services_run(self):
        d = Diff(ALLOWED)
        for name in sorted(set(self.compose) - set(self.helm)):
            d.add(("service", name, "*"), f"{name}: compose runs it, the chart does not")
        for name in sorted(set(self.helm) - set(self.compose)):
            d.add(("service", name, "*"), f"{name}: the chart runs it, compose does not")
        self.report(d, ("service",))

    def test_Each_service_is_given_the_same_variables(self):
        d = Diff(ALLOWED)
        for name in sorted(set(self.compose) & set(self.helm)):
            for var in sorted(self.compose[name]["env"] - self.helm[name]["env"]):
                d.add(("env", name, var), f"{name}: {var} in compose, not in the chart")
            for var in sorted(self.helm[name]["env"] - self.compose[name]["env"]):
                d.add(("env", name, var), f"{name}: {var} in the chart, not in compose")
        self.report(d, ("env",))

    def test_Each_service_mounts_the_same_paths(self):
        d = Diff(ALLOWED)
        for name in sorted(set(self.compose) & set(self.helm)):
            mine, theirs = self.compose[name]["mounts"], self.helm[name]["mounts"]
            for p in sorted(p for p in mine if not covered(p, theirs)):
                d.add(("mount", name, p), f"{name}: mounts {p} in compose, not in the chart")
            for p in sorted(p for p in theirs if not covered(p, mine)):
                d.add(("mount", name, p), f"{name}: mounts {p} in the chart, not in compose")
        self.report(d, ("mount",))

    def test_Every_file_and_script_compose_mounts_from_deploy_the_chart_carries(self):
        copies = {Path(v) for v in chart_copies().values()}
        problems = []
        for name, s in sorted(self.compose.items()):
            if ("service", name, "*") in ALLOWED:
                continue
            for target, source in sorted(s["binds"].items()):
                src = Path(source).resolve()
                try:
                    rel = src.relative_to(DEPLOY.resolve())
                except ValueError:
                    continue
                if rel.parts[0] not in ("config", "services"):
                    continue
                files = [p.relative_to(DEPLOY.resolve()) for p in sorted(src.rglob("*")) if p.is_file()] if src.is_dir() else [rel]
                # A folder of rules or exporters: each file in it that compose's service reads.
                files = [f for f in files if not f.name.startswith((".", "README")) and "__pycache__" not in f.parts]
                for f in files:
                    if f not in copies:
                        problems.append(f"{name}: compose mounts deploy/{f} at {target}; the chart does not carry it "
                                        "(files/, and check-chart.py's COPIES)")
        self.assertEqual(problems, [])


class PodmanTests(unittest.TestCase):
    """podman.yml on top changes how a service runs (GPUs, users, sockets), not which ones or what they are given."""

    @classmethod
    def setUpClass(cls):
        cls.docker = compose_services(compose_config("docker-compose.yml"))
        cls.podman = compose_services(compose_config("docker-compose.yml", "podman.yml"))

    def test_The_same_services_variables_and_mounts(self):
        d = Diff(PODMAN_ALLOWED)
        for name in sorted(set(self.docker) ^ set(self.podman)):
            d.add(("service", name, "*"), f"{name}: in one of compose and podman.yml only")
        for name in sorted(set(self.docker) & set(self.podman)):
            for var in sorted(self.docker[name]["env"] ^ self.podman[name]["env"]):
                d.add(("env", name, var), f"{name}: {var} differs under Podman")
            for p in sorted(self.docker[name]["mounts"] ^ self.podman[name]["mounts"]):
                d.add(("mount", name, p), f"{name}: mount {p} differs under Podman")
        self.assertEqual(d.problems + d.stale(("service", "env", "mount")), [])

    def test_Scale_out_runs_the_same_services_with_the_same_variables(self):
        scaled = compose_services(compose_config("docker-compose.yml", "scale.yml"))
        self.assertEqual(set(scaled), set(self.docker))
        for name in self.docker:
            extra = scaled[name]["env"] ^ self.docker[name]["env"]
            # Traefik lists the replicas by name (config/traefik/routes.yml): PROJECT-app-N.
            self.assertEqual(extra, {"APP_REPLICAS", "COMPOSE_PROJECT_NAME"} if name == "traefik" else set(), name)
        self.assertEqual(compose_config("docker-compose.yml", "scale.yml")["services"]["app"]["deploy"]["replicas"], 2)


class SecretsAndRoutesTests(unittest.TestCase):
    """Text only: no compose or helm needed."""

    def test_The_chart_makes_or_takes_every_secret_env_example_asks_for(self):
        example = (DEPLOY / ".env.example").read_text()
        block = example.split("# Secrets.", 1)[1].split("\n\n", 1)[0]
        required = set(re.findall(r"^([A-Z_]+)=", block, re.M))
        secrets = (CHART / "templates" / "secrets.yaml").read_text()
        made = set(re.findall(r'^\s+"([A-Z_]+)" \(list', secrets, re.M))
        optional = set(re.findall(r'"([A-Z_]+)" \$s\.\w+', secrets))
        self.assertEqual(required, made)
        # The optional ones .env.example names (HF_TOKEN, GitLab's sign-in) the chart takes from its Secret too.
        for key in ("HF_TOKEN", "GITLAB_TOKEN", "GITLAB_USERNAME", "GITLAB_PASSWORD"):
            self.assertIn(f"# {key}=", example)
            self.assertIn(key, optional)

    def test_The_ingress_sends_the_app_s_paths_where_Traefik_does(self):
        routes = (DEPLOY / "config" / "traefik" / "routes.yml").read_text()
        ingress = (CHART / "templates" / "ingress.yaml").read_text()
        app_rule = re.search(r"^\s+app:\n\s+rule: (.*)$", routes, re.M).group(1)
        traefik_app = set(re.findall(r"Path(?:Prefix)?\(`([^`]+)`\)", app_rule))
        lists = re.findall(r'range list ((?:"[^"]+" ?)+)', ingress)
        helm_app = set(re.findall(r'"([^"]+)"', lists[0]))
        self.assertEqual(traefik_app, helm_app)
        # The gateway's paths the app takes (speech and pictures, and asking a model through the key's line).
        gateway = set()
        for name in ("gateway-speech", "gateway-completions"):
            rule = re.search(rf"^\s+{name}:\n\s+rule: (.*)$", routes, re.M).group(1)
            gateway |= set(re.findall(r"Path\(`([^`]+)`\)", rule))
        self.assertEqual(gateway, set(re.findall(r'"([^"]+)"', lists[1])))


class StaticReaderTests(unittest.TestCase):
    """The chart read as text finds what the templates hold (the reader the other tests use without helm)."""

    def test_It_finds_containers_variables_and_mounts(self):
        helm = helm_static()
        self.assertIn("ARGUS_KEY_CHECK_URL", helm["argus"]["env"])
        self.assertIn("Auth__AdminPassword", helm["app"]["env"])
        self.assertIn("DATABASE_URL", helm["litellm"]["env"])
        self.assertEqual(helm["audio"]["env"], {"HF_HUB_DISABLE_XET", "STT_MODEL_TTL", "TTS_MODEL_TTL"})
        self.assertIn("/sd-serve.sh", helm["videogen"]["mounts"])
        self.assertIn("/app/exporter.py", helm["cpu-temp-exporter"]["mounts"])
        self.assertNotIn("http", helm)

    def test_It_reads_what_helm_renders(self):
        rendered = helm_rendered()
        if rendered is None:
            self.skipTest("no helm (or no PyYAML) here")
        static = helm_static()
        # The Prometheus rules' mounts are named by a range over the chart's files: rendered only.
        for chart in (static, rendered):
            for c in chart.values():
                c["mounts"] = {m for m in c["mounts"] if "/rules/" not in m}
        self.assertEqual(static, rendered)


if __name__ == "__main__":
    unittest.main()
