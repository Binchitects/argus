"""
scripts/kube-install.sh (installer.sh --kubernetes) against fake kubectl, helm and docker on PATH: a cluster that is
a JSON file (its version, namespaces, rights, storage classes, nodes, ingress classes, issuers, secrets) and a log
of every call. What must hold: each requirement is checked and its lack said with what to do, before anything is
changed; the secrets are made in the cluster from stdin and never appear in a command line, the output or the
values file; helm installs atomically and waits; an upgrade keeps the release's values and secrets; air-gapped
images go to the registry and the chart is told so; a purge needs its word.
"""
from __future__ import annotations

import gzip
import json
import shutil
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "deploy" / "scripts" / "kube-install.sh"
CHART = REPO / "deploy" / "helm" / "argus-arena"

FAKE_KUBECTL = r'''#!/usr/bin/env python3
import json, os, sys
state_path = os.environ["FAKE_KUBE"]
s = json.load(open(state_path))
args = sys.argv[1:]
stdin = sys.stdin.read() if "-f" in args and "-" in args else ""
with open(os.environ["FAKE_LOG"], "a") as log:
    log.write(json.dumps(["kubectl"] + args) + "\n")
ns = None
rest = []
i = 0
while i < len(args):
    if args[i] in ("--context", "-n", "--namespace"):
        if args[i] != "--context":
            ns = args[i + 1]
        i += 2
        continue
    rest.append(args[i])
    i += 1
def save():
    json.dump(s, open(state_path, "w"))
def out(v):
    print(json.dumps(v) if not isinstance(v, str) else v)
    sys.exit(0)
a = rest
if a[:2] == ["version", "--client"]:
    out({"clientVersion": {"major": "1", "minor": "31"}})
if a[:1] == ["version"]:
    if not s.get("reachable", True):
        sys.exit(1)
    out({"clientVersion": {"major": "1", "minor": "31"}, "serverVersion": {"major": "1", "minor": s.get("minor", "30")}})
if a[:2] == ["auth", "can-i"]:
    out("no" if a[3] in s.get("denied", []) else "yes")
if a[:2] == ["get", "namespace"]:
    sys.exit(0 if a[2] in s.get("namespaces", []) else 1)
if a[:2] == ["create", "namespace"]:
    s.setdefault("namespaces", []).append(a[2]); save(); sys.exit(0)
if a[:2] == ["get", "storageclass"]:
    out({"items": [{"metadata": {"name": n, "annotations": {"storageclass.kubernetes.io/is-default-class": "true"} if n == s.get("default_class") else {}}} for n in s.get("classes", [])]})
if a[:2] == ["get", "nodes"]:
    out({"items": [{"status": {"allocatable": n}} for n in s.get("nodes", [])]})
if a[:2] == ["get", "ingressclass"]:
    out({"items": [{"metadata": {"name": n}} for n in s.get("ingress", [])]})
if a[:2] == ["get", "clusterissuer"]:
    sys.exit(0 if a[2] in s.get("issuers", []) else 1)
if a[:2] == ["get", "apiservice"]:
    sys.exit(0 if s.get("metrics") else 1)
if a[:2] == ["get", "secret"]:
    secret = s.get("secrets", {}).get(f"{ns}/{a[2]}")
    if secret is None:
        sys.exit(1)
    if "-o" in a:
        out(secret["type"])
    sys.exit(0)
if a[:3] == ["apply", "-f", "-"]:
    name = next(l.split(":", 1)[1].strip() for l in stdin.splitlines() if l.strip().startswith("name:"))
    s.setdefault("secrets", {})[f"{ns}/{name}"] = {"type": "Opaque", "manifest": stdin}
    save(); sys.exit(0)
if a[:2] == ["get", "--raw"]:
    if s.get("installed") and not s.get("app_down"):
        out({"name": "Argus Arena", "version": "7.3.0"})
    sys.exit(1)
if a[:2] == ["delete", "pvc"] or a[:2] == ["delete", "secret"]:
    sys.exit(0)
if a[:1] == ["get"]:
    out("NAME READY")
sys.exit(1)
'''

FAKE_HELM = r'''#!/usr/bin/env python3
import json, os, sys
state_path = os.environ["FAKE_KUBE"]
s = json.load(open(state_path))
args = [a for a in sys.argv[1:]]
with open(os.environ["FAKE_LOG"], "a") as log:
    log.write(json.dumps(["helm"] + args) + "\n")
if "--kube-context" in args:
    i = args.index("--kube-context"); del args[i:i + 2]
if args[:1] == ["version"]:
    print("v" + s.get("helm", "3.16.2")); sys.exit(0)
if args[:1] == ["status"]:
    sys.exit(0 if s.get("installed") else 1)
if args[:2] == ["get", "values"]:
    print(json.dumps(s.get("values", {}))); sys.exit(0)
if args[:1] == ["upgrade"]:
    if s.get("helm_fails"):
        sys.exit(1)
    s["installed"] = True
    for i, a in enumerate(args):
        if a == "-f":
            s.setdefault("values_files", []).append(open(args[i + 1]).read())
    json.dump(s, open(state_path, "w")); sys.exit(0)
if args[:1] == ["uninstall"]:
    s["installed"] = False; json.dump(s, open(state_path, "w")); sys.exit(0)
sys.exit(1)
'''

FAKE_DOCKER = r'''#!/usr/bin/env python3
import json, os, sys
with open(os.environ["FAKE_LOG"], "a") as log:
    log.write(json.dumps(["docker"] + sys.argv[1:]) + "\n")
if sys.argv[1:3] == ["image", "inspect"]:
    sys.exit(1)
if sys.argv[1:2] == ["load"]:
    sys.stdin.buffer.read()
sys.exit(0)
'''

GOOD = {
    "namespaces": [], "classes": ["standard", "nfs"], "default_class": "standard",
    "nodes": [{"cpu": "16", "memory": "64Gi", "nvidia.com/gpu": "1"}, {"cpu": "8000m", "memory": "32Gi"}],
    "ingress": ["nginx"], "issuers": ["letsencrypt"], "metrics": True,
}


class KubeInstall(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="kube-install-"))
        self.bin = self.dir / "bin"
        self.bin.mkdir()
        for name, text in (("kubectl", FAKE_KUBECTL), ("helm", FAKE_HELM), ("docker", FAKE_DOCKER)):
            p = self.bin / name
            p.write_text(text)
            p.chmod(p.stat().st_mode | stat.S_IXUSR)
        self.state = self.dir / "cluster.json"
        self.log = self.dir / "calls.log"
        self.keep = self.dir / "keep"
        self.cluster(GOOD)

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def cluster(self, base, **more):
        self.state.write_text(json.dumps({**base, **more}))

    def now(self):
        return json.loads(self.state.read_text())

    def calls(self, tool=None):
        if not self.log.exists():
            return []
        rows = [json.loads(l) for l in self.log.read_text().splitlines()]
        return [r for r in rows if tool is None or r[0] == tool]

    def run_it(self, *args, script=SCRIPT):
        env = {"PATH": f"{self.bin}:/usr/bin:/bin", "FAKE_KUBE": str(self.state), "FAKE_LOG": str(self.log), "HOME": str(self.dir)}
        return subprocess.run(["bash", str(script), *args, "--dir", str(self.keep)], capture_output=True, text=True, env=env, stdin=subprocess.DEVNULL, timeout=120)

    def test_A_cluster_with_what_it_needs_passes_its_requirements(self):
        r = self.run_it("requirements", "--cluster-issuer", "letsencrypt", "--shared-storage-class", "nfs", "--autoscale")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        for said in ("kubectl 1.31", "helm 3.16.2", "Kubernetes 1.30", "the default storage class: standard", "shared storage class nfs",
                     "ingress controller: nginx", "ClusterIssuer letsencrypt", "1 GPU(s)", "24 CPUs and 96 GiB", "the metrics server"):
            self.assertIn(said, r.stdout)
        self.assertFalse([c for c in self.calls() if c[1] in ("create", "apply") or c[1:2] == ["upgrade"]], "requirements changed nothing")

    def test_What_a_cluster_lacks_is_each_said_with_what_to_do_and_nothing_is_changed(self):
        self.cluster(GOOD, classes=["standard"], default_class=None, ingress=[], metrics=False, denied=["networkpolicies"],
                     nodes=[{"cpu": "2", "memory": "4Gi"}, {"cpu": "2", "memory": "4Gi"}])
        r = self.run_it("install", "--domain", "arena.example.test", "--autoscale", "--gpu", "yes")
        self.assertEqual(r.returncode, 3, r.stdout + r.stderr)
        for said in ("the cluster has no default; name one with --storage-class", "ReadWriteMany", "an ingress controller",
                     "the TLS: --tls-secret NAME", "you may not make networkpolicies", "install NVIDIA's GPU operator",
                     "the stack needs 8 CPUs and 24 GiB", "install metrics-server"):
            self.assertIn(said, r.stdout)
        self.assertIn("requirement(s) not met", r.stderr)
        self.assertFalse([c for c in self.calls() if c[1:2] in (["create"], ["upgrade"]) or "apply" in c], "nothing was changed")

    def test_An_unreachable_cluster_is_said_first(self):
        self.cluster(GOOD, reachable=False)
        r = self.run_it("requirements", "--context", "prod")
        self.assertEqual(r.returncode, 3)
        self.assertIn("the cluster does not answer: check kubectl's context", r.stdout)
        self.assertIn(["kubectl", "--context", "prod", "version", "-o", "json"], self.calls("kubectl"))

    def test_An_install_makes_the_secrets_from_stdin_installs_atomically_and_checks_the_app(self):
        r = self.run_it("install", "--domain", "arena.example.test", "--cluster-issuer", "letsencrypt", "--shared-storage-class", "nfs", "--yes")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        cluster = self.now()
        self.assertIn("arena", cluster["namespaces"])
        manifest = cluster["secrets"]["arena/arena-secrets"]["manifest"]
        secrets = [l.split(":", 1)[1].strip().strip('"') for l in manifest.splitlines() if l.startswith("  ") and l.strip().split(":")[0].isupper()]
        self.assertEqual(7, len(secrets))
        self.assertTrue(all(len(v) >= 24 for v in secrets))
        self.assertTrue(manifest.count("GATEWAY_KEY: \"sk-") == 1)
        # Never on a command line, in the output, or in the values file.
        values = (self.keep / "arena-values.yaml").read_text()
        everything = self.log.read_text() + r.stdout + r.stderr + values
        for v in secrets:
            self.assertNotIn(v, everything)
        self.assertIn("base64 -d", r.stdout)
        upgrade = next(c for c in self.calls("helm") if c[1] == "upgrade")
        for flag in ("--install", "--atomic", "--wait"):
            self.assertIn(flag, upgrade)
        self.assertIn('existingSecret: "arena-secrets"', values)
        self.assertIn('cert-manager.io/cluster-issuer: "letsencrypt"', values)
        self.assertIn('sharedStorageClass: "nfs", sharedAccessMode: ReadWriteMany', values)
        self.assertNotIn("llamacpp: { enabled: false }", values, "a GPU node: the GPU servers stay on")
        self.assertIn("the app answers: Argus Arena 7.3.0", r.stdout)
        self.assertEqual(0o600, (self.keep / "arena-values.yaml").stat().st_mode & 0o777)

    def test_Without_GPUs_the_GPU_servers_are_left_out_and_secrets_already_there_are_kept(self):
        self.cluster(GOOD, nodes=[{"cpu": "16", "memory": "64Gi"}], namespaces=["arena"],
                     secrets={"arena/arena-secrets": {"type": "Opaque"}, "arena/arena-tls": {"type": "kubernetes.io/tls"}})
        r = self.run_it("install", "--domain", "arena.example.test", "--tls-secret", "arena-tls", "--yes")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("secrets kept: arena-secrets", r.stdout)
        self.assertFalse([c for c in self.calls("kubectl") if "apply" in c], "APP_KEY never changes")
        values = (self.keep / "arena-values.yaml").read_text()
        for off in ("llamacpp", "imagegen", "videogen", "vllm", "studio"):
            self.assertIn(f"{off}: {{ enabled: false }}", values)
        self.assertIn('tlsSecret: "arena-tls"', values)
        self.assertIn("sharedAccessMode: ReadWriteOnce", values, "one node: its own volumes")

    def test_An_installed_release_is_upgraded_not_installed_again_keeping_its_values(self):
        self.cluster(GOOD, installed=True, namespaces=["arena"], values={"domain": "arena.example.test"},
                     secrets={"arena/arena-secrets": {"type": "Opaque"}})
        again = self.run_it("install", "--domain", "arena.example.test", "--cluster-issuer", "letsencrypt", "--yes")
        self.assertEqual(again.returncode, 3)
        self.assertIn("installed in arena already: upgrade it", again.stderr)
        r = self.run_it("upgrade", "--yes")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        upgrade = next(c for c in self.calls("helm") if c[1] == "upgrade")
        self.assertIn("--reset-then-reuse-values", upgrade)
        self.assertIn("--atomic", upgrade)
        self.assertNotIn("--install", upgrade)
        self.assertFalse([c for c in self.calls("kubectl") if "apply" in c])

    def test_The_features_asked_for_are_the_charts_values_and_bring_what_they_need(self):
        r = self.run_it("install", "--domain", "arena.example.test", "--cluster-issuer", "letsencrypt", "--shared-storage-class", "nfs", "--yes",
                        "--without", "video", "--with", "laya,lean")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        values = (self.keep / "arena-values.yaml").read_text()
        for line in ("videogen: { enabled: false }", "laya: { enabled: true }", "lean: { enabled: true }", "imagegen: { enabled: true }"):
            self.assertIn(line, values)
        self.assertIn("app: { replicas: 2, features: [meetings, lab, apps, data, decks, media, agents, code-studio] }", values)
        for args, message in [
            (["--without", "pictures"], "pictures cannot be off while media needs it"),
            (["--with", "browser"], "browser is not in the chart yet"),
            (["--with", "nope"], "no feature nope"),
        ]:
            self.cluster(GOOD)
            r = self.run_it("install", "--domain", "arena.example.test", "--cluster-issuer", "letsencrypt", "--shared-storage-class", "nfs", "--yes", "--dry-run", *args)
            self.assertNotEqual(r.returncode, 0, args)
            self.assertIn(message, r.stderr, args)

    def test_An_upgrade_changes_only_the_features_asked_for_on_top_of_the_releases(self):
        self.cluster(GOOD, installed=True, namespaces=["arena"], secrets={"arena/arena-secrets": {"type": "Opaque"}},
                     values={"domain": "arena.example.test", "llamacpp": {"enabled": True}, "imagegen": {"enabled": True},
                             "laya": {"enabled": False}, "sandbox": {"enabled": True}, "app": {"replicas": 3, "features": ["lab", "media"]}})
        r = self.run_it("upgrade", "--yes", "--with", "laya")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        files = self.now()["values_files"]
        features = files[-1]
        self.assertIn("laya: { enabled: true }", features)
        self.assertIn("llamacpp: { enabled: true }", features)
        self.assertIn("videogen: { enabled: false }", features)
        # The release's replicas stay: only the features are given.
        self.assertIn("app: { features: [lab, media] }", features)
        r = self.run_it("upgrade", "--yes")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(len(self.now()["values_files"]), 1, "no change asked: no features file")

    def test_A_failed_helm_run_is_said_and_nothing_is_said_to_run(self):
        r = self.run_it("install", "--domain", "arena.example.test", "--cluster-issuer", "letsencrypt", "--shared-storage-class", "nfs", "--yes")
        self.cluster({**self.now(), "installed": False, "helm_fails": True})
        r = self.run_it("install", "--domain", "arena.example.test", "--cluster-issuer", "letsencrypt", "--shared-storage-class", "nfs", "--yes")
        self.assertEqual(r.returncode, 1)
        self.assertIn("nothing is left running (--atomic)", r.stderr)

    def test_Air_gapped_the_bundles_images_go_to_the_registry_and_the_chart_pulls_them_from_there(self):
        bundle = self.dir / "bundle"
        (bundle / "images").mkdir(parents=True)
        (bundle / "deploy" / "scripts").mkdir(parents=True)
        shutil.copy(SCRIPT, bundle / "deploy" / "scripts" / "kube-install.sh")
        shutil.copytree(CHART, bundle / "deploy" / "helm" / "argus-arena")
        shutil.copy(REPO / "deploy" / "features", bundle / "deploy" / "features")
        rows = [("arena-app:7.3.0", "app.tar.gz"), ("arena-web:7.3.0", "web.tar.gz"), ("pgvector/pgvector:0.8.0-pg16", "pg.tar.gz"),
                ("ghcr.io/berriai/litellm:main-stable", "litellm.tar.gz")]
        for _, f in rows:
            (bundle / "images" / f).write_bytes(gzip.compress(b"image"))
        (bundle / "images" / "IMAGES").write_text("".join(f"{ref}\t{f}\tsha256:x\t1\t1\n" for ref, f in rows))
        r = self.run_it("install", "--domain", "arena.example.test", "--cluster-issuer", "letsencrypt", "--shared-storage-class", "nfs",
                        "--registry", "registry.example.test/arena", "--pull-secret", "regcred", "--yes", script=bundle / "deploy" / "scripts" / "kube-install.sh")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        pushed = [c[2] for c in self.calls("docker") if c[1] == "push"]
        self.assertEqual(["registry.example.test/arena/arena-app:7.3.0", "registry.example.test/arena/arena-web:7.3.0",
                          "registry.example.test/arena/pgvector:0.8.0-pg16", "registry.example.test/arena/litellm:main-stable"], pushed)
        values = (self.keep / "arena-values.yaml").read_text()
        self.assertIn('  app: { repository: "registry.example.test/arena/arena-app", tag: "7.3.0" }', values)
        self.assertIn('  postgres: { repository: "registry.example.test/arena/pgvector", tag: "0.8.0-pg16" }', values)
        self.assertIn('  litellm: { repository: "registry.example.test/arena/litellm", tag: "main-stable" }', values)
        self.assertIn('pullSecrets: ["regcred"]', values)

    def test_A_dry_run_shows_the_plan_and_changes_nothing(self):
        r = self.run_it("install", "--domain", "arena.example.test", "--cluster-issuer", "letsencrypt", "--shared-storage-class", "nfs", "--dry-run")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("would make the Secret arena-secrets", r.stdout)
        self.assertIn("would run: helm upgrade --install arena", r.stdout)
        self.assertFalse([c for c in self.calls() if c[1:2] in (["create"], ["upgrade"]) or "apply" in c])
        self.assertFalse((self.keep / "arena-values.yaml").exists())

    def test_A_purge_needs_its_word_and_then_takes_the_volumes_and_secrets(self):
        self.cluster(GOOD, installed=True, namespaces=["arena"])
        r = self.run_it("remove", "--purge")
        self.assertEqual(r.returncode, 3)
        self.assertIn("confirm with the word PURGE", r.stderr)
        self.assertFalse([c for c in self.calls("helm") if c[1] == "uninstall"])
        r = self.run_it("remove", "--purge", "--confirm", "PURGE")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertTrue([c for c in self.calls("helm") if c[1] == "uninstall"])
        self.assertTrue([c for c in self.calls("kubectl") if "pvc" in c and "delete" in c])
        self.assertTrue([c for c in self.calls("kubectl") if "arena-secrets" in c and "delete" in c])

    def test_The_offline_installer_hands_kubernetes_to_it(self):
        r = self.run_it("requirements", "--kubernetes", "--cluster-issuer", "letsencrypt", "--shared-storage-class", "nfs",
                        script=REPO / "deploy" / "scripts" / "installer.sh")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("The cluster has what Argus Arena needs.", r.stdout)


if __name__ == "__main__":
    unittest.main()
