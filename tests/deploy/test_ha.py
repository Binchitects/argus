"""
The gateway as several replicas sharing their counts in Valkey (deploy/ha.yml, the ha profile; Helm valkey.enabled), read
with `docker compose config` (or Podman's) and the chart, without starting or pulling anything: off by default, on with
the file, and the gateway's config reading Valkey only from the environment it is given.
"""
from __future__ import annotations

import json
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from support import REPO

DEPLOY = REPO / "deploy"


def config(*files: str) -> dict:
    for engine in ("docker", "podman"):
        exe = shutil.which(engine)
        if exe and subprocess.run([exe, "compose", "version"], capture_output=True).returncode == 0:
            break
    else:
        raise unittest.SkipTest("no compose here")
    env = (DEPLOY / ".env.example").read_text()
    env = re.sub(r"(?m)^(ADMIN_PASSWORD|DB_PASSWORD|APP_KEY|ENGINE_KEY|ARGUS_KEY|GATEWAY_KEY)=$", r"\1=x", env)
    env = re.sub(r"(?m)^DATA_DIR=$", "DATA_DIR=/srv/arena/data", env)

    with tempfile.NamedTemporaryFile("w", suffix=".env") as f:
        f.write(env)
        f.flush()
        args = [exe, "compose", "--env-file", f.name]
        for name in files:
            args += ["-f", str(DEPLOY / name)]
        r = subprocess.run(args + ["config", "--format", "json"], capture_output=True, text=True, timeout=120)
    if r.returncode != 0:
        raise AssertionError(r.stderr[-2000:])
    return json.loads(r.stdout[r.stdout.index("{"):])


class HaTests(unittest.TestCase):
    def test_off_by_default_the_gateway_counts_alone(self):
        c = config("docker-compose.yml")
        self.assertNotIn("valkey", c["services"])
        litellm = c["services"]["litellm"]
        self.assertNotIn("REDIS_HOST", litellm.get("environment") or {})
        self.assertEqual("litellm", litellm.get("container_name"))

    def test_with_ha_yml_two_gateway_replicas_share_valkey(self):
        c = config("docker-compose.yml", "ha.yml")
        litellm = c["services"]["litellm"]
        self.assertEqual(2, litellm["deploy"]["replicas"])
        self.assertNotIn("container_name", litellm)
        self.assertEqual(("valkey", "6379"), (litellm["environment"]["REDIS_HOST"], litellm["environment"]["REDIS_PORT"]))
        valkey = c["services"]["valkey"]
        self.assertIn("--appendonly", valkey["command"])
        self.assertNotIn("ports", valkey)
        self.assertTrue(valkey["image"].startswith("public.ecr.aws/"))

    def test_the_gateway_reads_valkey_only_from_its_environment(self):
        text = (DEPLOY / "config" / "litellm.yaml").read_text()
        self.assertIn("redis_host: os.environ/REDIS_HOST", text)
        self.assertIn("redis_port: os.environ/REDIS_PORT", text)
        self.assertEqual(text, (DEPLOY / "helm" / "argus-arena" / "files" / "litellm.yaml").read_text())

    def test_the_chart_runs_valkey_only_when_asked(self):
        helm = shutil.which("helm")
        if helm is None:
            self.skipTest("no helm here")
        chart = DEPLOY / "helm" / "argus-arena"
        off = subprocess.run([helm, "template", "arena", str(chart)], capture_output=True, text=True, timeout=120).stdout
        on = subprocess.run([helm, "template", "arena", str(chart), "--set", "valkey.enabled=true", "--set", "litellm.replicas=2"],
                            capture_output=True, text=True, timeout=120).stdout
        # Checked without printing the render: it holds the Secret helm made up.
        self.assertFalse("name: valkey\n" in off, "valkey rendered while off")
        self.assertFalse("{ name: REDIS_HOST" in off, "REDIS_HOST given while off")
        self.assertTrue("name: valkey\n" in on, "no valkey when on")
        self.assertTrue("{ name: REDIS_HOST, value: valkey }" in on, "no REDIS_HOST when on")
        self.assertTrue(re.search(r"name: litellm\n(?:.*\n){0,6}?spec:\n  replicas: 2", on) is not None, "litellm not at 2 replicas")


if __name__ == "__main__":
    unittest.main()


class HaConfigTests(unittest.TestCase):
    def test_the_ha_gateway_config_is_the_plain_one_plus_its_counts_in_valkey(self):
        plain = (DEPLOY / "config" / "litellm.yaml").read_text()
        ha = (DEPLOY / "config" / "litellm-ha.yaml").read_text()
        added = "\n".join(line for line in ha.splitlines()[1:] if line not in plain.splitlines())
        self.assertIn("supported_call_types: []", added)
        self.assertIn("host: os.environ/REDIS_HOST", added)
        # Nothing of the plain config is missing from it.
        self.assertTrue(all(line in ha.splitlines() for line in plain.splitlines()))
        self.assertIn("litellm-ha.yaml", (DEPLOY / "ha.yml").read_text())
