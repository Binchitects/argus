"""
deploy/services/vllm.Dockerfile (arena-vllm) and deploy/vllm-build.yml, read without building or pulling anything: vLLM and
the base pinned, LMCache only when asked for, the supervisor and probe copied from the build context (where they are), models
never fetched by vLLM itself, and compose resolving the build.
"""
from __future__ import annotations

import re
import shutil
import subprocess
import unittest

from support import REPO

DEPLOY = REPO / "deploy"
DOCKERFILE = DEPLOY / "services" / "vllm.Dockerfile"


class VllmImageTests(unittest.TestCase):
    def setUp(self):
        self.text = DOCKERFILE.read_text()

    def test_vllm_and_its_base_are_pinned_and_lmcache_is_optional(self):
        self.assertRegex(self.text, r"(?m)^ARG VLLM_VERSION=\d+\.\d+\.\d+$")
        self.assertRegex(self.text, r"(?m)^ARG BASE_IMAGE=python:3\.\d+-slim$")
        self.assertRegex(self.text, r"(?m)^ARG LMCACHE_VERSION=$")
        self.assertIn('"vllm==${VLLM_VERSION}"', self.text)
        self.assertIn('if [ -n "${LMCACHE_VERSION}" ]', self.text)
        self.assertNotRegex(self.text, r":latest\b")
        self.assertNotIn("docker.io", self.text)

    def test_it_runs_the_supervisor_offline_and_copies_only_what_the_context_has(self):
        self.assertIn('ENTRYPOINT ["/bin/sh", "/vllm/supervisor.sh"]', self.text)
        self.assertIn("HF_HUB_OFFLINE=1", self.text)
        context = DEPLOY / "services"
        for line in re.findall(r"(?m)^COPY (.+)$", self.text):
            for src in line.split()[:-1]:
                self.assertTrue((context / src).exists(), src)
        # No secret in the image: the key reaches the servers by the environment at run time.
        self.assertNotRegex(self.text, r"(?i)(api[_-]?key|token|password)=")

    def test_compose_builds_it_only_with_the_build_file(self):
        docker = shutil.which("docker")
        if docker is None:
            self.skipTest("no docker here")
        env = (DEPLOY / ".env.example").read_text()
        env = re.sub(r"(?m)^(ADMIN_PASSWORD|DB_PASSWORD|APP_KEY|ENGINE_KEY|ARGUS_KEY|GATEWAY_KEY)=$", r"\1=x", env)
        env = re.sub(r"(?m)^DATA_DIR=$", "DATA_DIR=/srv/arena/data", env)

        import tempfile
        with tempfile.NamedTemporaryFile("w", suffix=".env") as f:
            f.write(env)
            f.flush()
            base = [docker, "compose", "--env-file", f.name, "--profile", "vllm", "-f", str(DEPLOY / "docker-compose.yml")]
            plain = subprocess.run(base + ["config", "vllm"], capture_output=True, text=True, timeout=120)
            built = subprocess.run(base + ["-f", str(DEPLOY / "vllm-build.yml"), "config", "vllm"], capture_output=True, text=True, timeout=120)
        self.assertEqual(0, plain.returncode, plain.stderr)
        self.assertEqual(0, built.returncode, built.stderr)
        self.assertNotIn("build:", plain.stdout)
        self.assertIn("dockerfile: vllm.Dockerfile", built.stdout)
        self.assertIn("image: arena-vllm:0.31.0", built.stdout)


if __name__ == "__main__":
    unittest.main()
