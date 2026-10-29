"""
deploy/services/sglang.Dockerfile (arena-sglang), deploy/sglang-build.yml and services/sglang/serve.py, read without
building or pulling anything: SGLang and the base pinned, the supervisor run as SGLang's (ENGINE_SERVER=sglang) with models
never fetched by SGLang itself, the launcher keeping the key off the command line, and compose resolving the build and the
pinned image it runs otherwise.
"""
from __future__ import annotations

import re
import shutil
import subprocess
import tempfile
import unittest

from support import REPO

DEPLOY = REPO / "deploy"
DOCKERFILE = DEPLOY / "services" / "sglang.Dockerfile"
SERVE = DEPLOY / "services" / "sglang" / "serve.py"


class SglangImageTests(unittest.TestCase):
    def setUp(self):
        self.text = DOCKERFILE.read_text()

    def test_sglang_and_its_base_are_pinned(self):
        self.assertRegex(self.text, r"(?m)^ARG SGLANG_VERSION=\d+\.\d+\.\d+$")
        self.assertRegex(self.text, r"(?m)^ARG BASE_IMAGE=python:3\.\d+-slim$")
        self.assertIn('"sglang==${SGLANG_VERSION}"', self.text)
        self.assertNotRegex(self.text, r":latest\b")

    def test_it_runs_the_supervisor_as_sglang_offline_and_copies_only_what_the_context_has(self):
        self.assertIn('ENTRYPOINT ["/bin/sh", "/vllm/supervisor.sh"]', self.text)
        self.assertIn("ENGINE_SERVER=sglang", self.text)
        self.assertIn("HF_HUB_OFFLINE=1", self.text)
        context = DEPLOY / "services"
        for line in re.findall(r"(?m)^COPY (.+)$", self.text):
            for src in line.split()[:-1]:
                self.assertTrue((context / src).exists(), src)
        self.assertNotRegex(self.text, r"(?i)(api[_-]?key|token|password)=")

    def test_the_launcher_takes_the_key_from_the_environment_into_the_process_only(self):
        text = SERVE.read_text()
        self.assertIn('os.environ.pop("SGLANG_API_KEY"', text)
        self.assertIn('runpy.run_module("sglang.launch_server"', text)
        # Added to sys.argv inside the process: /proc/PID/cmdline keeps the arguments it was started with.
        self.assertIn('sys.argv = ["sglang.launch_server", *sys.argv[1:], *(["--api-key", key] if key else [])]', text)

    def test_compose_runs_the_pinned_image_and_builds_it_only_with_the_build_file(self):
        docker = shutil.which("docker")
        if docker is None:
            self.skipTest("no docker here")
        env = (DEPLOY / ".env.example").read_text()
        env = re.sub(r"(?m)^(ADMIN_PASSWORD|DB_PASSWORD|APP_KEY|ENGINE_KEY|ARGUS_KEY|GATEWAY_KEY)=$", r"\1=x", env)
        env = re.sub(r"(?m)^DATA_DIR=$", "DATA_DIR=/srv/arena/data", env)
        with tempfile.NamedTemporaryFile("w", suffix=".env") as f:
            f.write(env)
            f.flush()
            base = [docker, "compose", "--env-file", f.name, "--profile", "sglang", "-f", str(DEPLOY / "docker-compose.yml")]
            plain = subprocess.run(base + ["config", "sglang"], capture_output=True, text=True, timeout=120)
            built = subprocess.run(base + ["-f", str(DEPLOY / "sglang-build.yml"), "config", "sglang"], capture_output=True, text=True, timeout=120)
        self.assertEqual(0, plain.returncode, plain.stderr)
        self.assertEqual(0, built.returncode, built.stderr)
        self.assertNotIn("build:", plain.stdout)
        self.assertIn("image: lmsysorg/sglang:v0.5.21-cu130-runtime", plain.stdout)
        self.assertIn("ENGINE_SERVER: sglang", plain.stdout)
        self.assertIn("dockerfile: sglang.Dockerfile", built.stdout)
        self.assertIn("image: arena-sglang:0.5.21", built.stdout)


if __name__ == "__main__":
    unittest.main()
