"""
What `helm template` renders, read as a cluster would take it (helm and PyYAML here, else skipped): every module on.
What must hold: no environment value is a number Go wrote in scientific notation (a quoted 31536000 became
"3.1536e+07", which the app cannot read as an int: it would not start); and no Ingress path that ingress-nginx's strict
path types refuse (a Prefix or Exact path with a character other than letters, digits, /, _ and -) — its admission
webhook turns the whole Ingress away; and no PersistentVolumeClaim that no pod mounts (where a claim binds only for its
first pod, WaitForFirstConsumer, it never binds and helm --wait waits for it until the install fails), with every module
on, with the defaults and with CI's kind values. All three were found by the install on a kind cluster. And a change to
an engine's scripts (subPath mounts of arena-config, which never see the ConfigMap change) rolls its pod, and no other
engine's: else `helm upgrade` leaves the old router or supervisor running, and Restart and Reload do nothing.
"""
from __future__ import annotations

import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CHART = REPO / "deploy" / "helm" / "arena"
KIND = REPO / "deploy" / "helm" / "kind-values.yaml"
ON = ["laya.enabled=true", "scanners.enabled=true", "studio.enabled=true", "workspaces.enabled=true", "blender.enabled=true", "lean.enabled=true", "videogen.enabled=true",
      "vllm.enabled=true", "sglang.enabled=true", "valkey.enabled=true", "availability.autoscale.enabled=true", "domain=arena.example.test"]
SCIENTIFIC = re.compile(r"^-?\d+(\.\d+)?e[+-]?\d+$", re.I)
STRICT_PATH = re.compile(r"^/[A-Za-z0-9/_-]*$")


def rendered(*args: str, chart: Path = CHART) -> list[dict] | None:
    helm = shutil.which("helm")
    try:
        import yaml  # noqa: PLC0415
    except ImportError:
        return None
    if helm is None:
        return None
    r = subprocess.run([helm, "template", "arena", str(chart), *args], capture_output=True, text=True, timeout=120)
    if r.returncode != 0:
        raise AssertionError(f"helm template failed: {r.stderr.strip()[-2000:]}")
    return [d for d in yaml.safe_load_all(r.stdout) if d]


class ChartRender(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.docs = rendered(*[a for s in ON for a in ("--set", s)])
        if cls.docs is None:
            raise unittest.SkipTest("needs helm and PyYAML")
        cls.defaults = rendered("--set", "domain=arena.example.test")
        cls.kind = rendered("-f", str(KIND), "--set", "domain=arena.example.test")

    def test_No_environment_value_is_a_number_in_scientific_notation(self):
        bad = []
        for d in self.docs:
            spec = (d.get("spec") or {}).get("template", {}).get("spec") or {}
            for c in spec.get("containers", []) + spec.get("initContainers", []):
                for e in c.get("env") or []:
                    if isinstance(e.get("value"), str) and SCIENTIFIC.match(e["value"]):
                        bad.append(f"{d['metadata']['name']}/{c['name']}: {e['name']}={e['value']}")
        self.assertEqual([], bad, "use | int before | quote")

    def test_Every_ingress_path_passes_ingress_nginx_strict_path_types(self):
        bad = []
        for d in self.docs:
            if d.get("kind") != "Ingress":
                continue
            for rule in d["spec"]["rules"]:
                for p in rule["http"]["paths"]:
                    if p["pathType"] in ("Prefix", "Exact") and not STRICT_PATH.match(p["path"]):
                        bad.append(f"{rule['host']}{p['path']} ({p['pathType']})")
        self.assertEqual([], bad, "make it ImplementationSpecific")

    def test_Every_claim_the_chart_makes_is_mounted_by_a_pod(self):
        for name, docs in (("every module on", self.docs), ("the defaults", self.defaults), ("kind's values", self.kind)):
            with self.subTest(name):
                made = {d["metadata"]["name"] for d in docs if d.get("kind") == "PersistentVolumeClaim"}
                mounted = set()
                for d in docs:
                    spec = (d.get("spec") or {}).get("template", {}).get("spec") or {}
                    mounted |= {v["persistentVolumeClaim"]["claimName"] for v in spec.get("volumes") or [] if "persistentVolumeClaim" in v}
                self.assertEqual(set(), made - mounted, "make the claim only when what mounts it is on")

    def test_A_change_to_an_engines_scripts_rolls_its_pod_and_no_other(self):
        def checksums(chart: Path, more: tuple[str, ...]) -> dict[str, str | None]:
            docs = rendered(*[a for s in ON + list(more) for a in ("--set", s)], chart=chart)
            return {d["metadata"]["name"]: (d["spec"]["template"]["metadata"].get("annotations") or {}).get("checksum/scripts")
                    for d in docs if d.get("kind") == "Deployment"}

        fake = ("vllm.fake=true", "sglang.fake=true")
        before = {(): checksums(CHART, ()), fake: checksums(CHART, fake)}
        self.assertEqual([], [e for e in ("llamacpp", "vllm", "sglang", "imagegen", "videogen") if not before[()][e]], "each engine has its checksum")
        for script, more, rolled in (("router.sh", (), {"llamacpp"}), ("vllm-supervisor.sh", (), {"vllm", "sglang"}), ("sglang-serve.py", (), {"sglang"}),
                                     ("vllm-serve.py", (), {"vllm"}), ("vllm-gguf-config.py", (), {"vllm", "sglang"}),
                                     ("sd-serve.sh", (), {"imagegen", "videogen"}), ("fake_vllm.py", (), set()), ("fake_vllm.py", fake, {"vllm", "sglang"}),
                                     ("fake-sglang-launch.py", fake, {"sglang"}), ("searxng.yml", (), set())):
            with self.subTest(script=script, more=more), tempfile.TemporaryDirectory() as tmp:
                chart = Path(tmp) / CHART.name
                shutil.copytree(CHART, chart)
                with open(chart / "files" / script, "a", encoding="utf-8") as f:
                    f.write("\n# changed\n")
                after = checksums(chart, more)
                self.assertEqual(rolled, {n for n, c in before[more].items() if after[n] != c})

    def test_The_app_names_no_gpu_to_the_media_servers_which_have_pods_and_gpus_of_their_own(self):
        # A pod's GPUs are numbered from 0 inside it: the engines' GPU 1 would be none there, and sd-server would find no CUDA device.
        for docs in (self.docs, self.defaults, self.kind):
            app = next(d for d in docs if d.get("kind") == "Deployment" and d["metadata"]["name"] == "app")
            env = {e["name"]: e.get("value") for c in app["spec"]["template"]["spec"]["containers"] for e in c.get("env") or []}
            self.assertEqual("true", env.get("Engine__MediaOwnGpus"))

    def test_The_workspaces_sandbox_is_asked_for_and_otherwise_the_pod_keeps_the_clusters_confinement(self):
        def pod(docs):
            d = next(d for d in docs if d.get("kind") == "Deployment" and d["metadata"]["name"] == "workspaces")
            return d["spec"]["template"]["spec"]
        plain = pod(self.docs)
        self.assertNotIn("hostUsers", plain)
        self.assertNotIn("procMount", plain["containers"][0]["securityContext"])
        boxed = pod(rendered(*[a for s in ON + ["workspaces.sandbox=true"] for a in ("--set", s)]))
        self.assertIs(boxed["hostUsers"], False)
        context = boxed["containers"][0]["securityContext"]
        self.assertEqual((context["procMount"], context["seccompProfile"]["type"], context["appArmorProfile"]["type"]), ("Unmasked", "Unconfined", "Unconfined"))
        # Still no capability of its own beyond switching users, and no way to gain one.
        self.assertIs(context["allowPrivilegeEscalation"], False)
        self.assertEqual(context["capabilities"]["drop"], ["ALL"])

    def test_forwarded_ports_hosts_reach_the_app_only_with_workspaces(self):
        def ingress(docs):
            return next(d for d in docs if d.get("kind") == "Ingress")["spec"]
        on = ingress(self.docs)
        wild = next(r for r in on["rules"] if r["host"] == "*.arena.example.test")
        self.assertEqual(wild["http"]["paths"][0]["backend"]["service"]["name"], "app")
        self.assertIn("*.arena.example.test", on["tls"][0]["hosts"])
        # The named hosts stay theirs (an exact host is matched before a wildcard).
        self.assertEqual([r["host"] for r in on["rules"]][:2], ["arena.example.test", "gateway.arena.example.test"])
        self.assertNotIn("*.arena.example.test", [r["host"] for r in ingress(self.defaults)["rules"]])


if __name__ == "__main__":
    unittest.main()
