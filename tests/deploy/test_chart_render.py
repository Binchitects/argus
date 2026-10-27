"""
What `helm template` renders, read as a cluster would take it (helm and PyYAML here, else skipped): every module on.
What must hold: no environment value is a number Go wrote in scientific notation (a quoted 31536000 became
"3.1536e+07", which the app cannot read as an int: it would not start); and no Ingress path that ingress-nginx's strict
path types refuse (a Prefix or Exact path with a character other than letters, digits, /, _ and -) — its admission
webhook turns the whole Ingress away. Both were found by the install on a kind cluster.
"""
from __future__ import annotations

import re
import shutil
import subprocess
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CHART = REPO / "deploy" / "helm" / "argus-arena"
ON = ["laya.enabled=true", "studio.enabled=true", "workspaces.enabled=true", "blender.enabled=true", "lean.enabled=true", "videogen.enabled=true",
      "vllm.enabled=true", "valkey.enabled=true", "availability.autoscale.enabled=true", "domain=arena.example.test"]
SCIENTIFIC = re.compile(r"^-?\d+(\.\d+)?e[+-]?\d+$", re.I)
STRICT_PATH = re.compile(r"^/[A-Za-z0-9/_-]*$")


def rendered() -> list[dict] | None:
    helm = shutil.which("helm")
    try:
        import yaml  # noqa: PLC0415
    except ImportError:
        return None
    if helm is None:
        return None
    r = subprocess.run([helm, "template", "arena", str(CHART), *[a for s in ON for a in ("--set", s)]], capture_output=True, text=True, timeout=120)
    if r.returncode != 0:
        raise AssertionError(f"helm template failed: {r.stderr.strip()[-2000:]}")
    return [d for d in yaml.safe_load_all(r.stdout) if d]


class ChartRender(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.docs = rendered()
        if cls.docs is None:
            raise unittest.SkipTest("needs helm and PyYAML")

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


if __name__ == "__main__":
    unittest.main()
