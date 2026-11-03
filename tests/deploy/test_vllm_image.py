"""
arena-vllm, read without building or pulling anything: deploy/services/vllm.Dockerfile (vLLM's own image, pinned, with its
GGUF plugin) is what compose builds and names, so ./arenactl build builds it with the vllm feature and make-installer.sh
ships it as the release's own; vllm-pypi.Dockerfile (vllm-build.yml) builds the same from PyPI. Both pin the plugin and gguf
by hash in a layer of their own after vLLM's, never let pip change vLLM or torch, check that the plugin's kernels load, add
the stack's rotary plugin for llama GGUF files after it, copy the supervisor and vLLM's launcher, and never fetch a model.
"""
from __future__ import annotations

import re
import shutil
import subprocess
import tempfile
import unittest

from support import REPO

DEPLOY = REPO / "deploy"
IMAGE = DEPLOY / "services" / "vllm.Dockerfile"
PYPI = DEPLOY / "services" / "vllm-pypi.Dockerfile"
PLUGIN = "vllm-gguf-plugin==0.0.5 --hash=sha256:e795779830ad8907cb0e51bf661dd7784ebaf244b0b324af07a8d3925b6a9739 --hash=sha256:36a1538d0ac6797c8b194c12d94eceed944fe945dbc2585830393cb4c3b92d5b"
GGUF = "gguf==0.19.0 --hash=sha256:70bcd10edfe697fb2dad6e40af2234b9d8ece9a41a99761405121ebda1c3c1cd"


def runs(text: str) -> list[str]:
    """A Dockerfile's RUN steps, each as one line."""
    return [r.replace("\\\n", " ") for r in re.findall(r"(?ms)^RUN .*?[^\\]$", text)]


ENV = tempfile.NamedTemporaryFile("w", suffix=".env")


def setUpModule():
    env = (DEPLOY / ".env.example").read_text()
    env = re.sub(r"(?m)^(ADMIN_PASSWORD|DB_PASSWORD|APP_KEY|ENGINE_KEY|ARGUS_KEY|GATEWAY_KEY)=$", r"\1=x", env)
    ENV.write(re.sub(r"(?m)^DATA_DIR=$", "DATA_DIR=/srv/arena/data", env))
    ENV.flush()


def tearDownModule():
    ENV.close()


def compose(*files: str, profiles: str = "vllm") -> list[str]:
    """docker compose with .env.example (placeholder secrets) and these profiles: the command, to which a subcommand is added."""
    cmd = [shutil.which("docker") or "docker", "compose", "--env-file", ENV.name, "-f", str(DEPLOY / "docker-compose.yml")]
    for extra in files:
        cmd += ["-f", str(DEPLOY / extra)]
    for p in profiles.split(","):
        cmd += ["--profile", p]
    return cmd


class VllmImageTests(unittest.TestCase):
    def test_arena_vllm_is_vllms_own_image_pinned_with_the_gguf_plugin_in_a_layer_of_its_own(self):
        text = IMAGE.read_text()
        self.assertIn("ARG VLLM_IMAGE=vllm/vllm-openai:v0.31.0\nFROM ${VLLM_IMAGE}", text)
        [plugin] = [r for r in runs(text) if "vllm-gguf-plugin" in r]
        self.assertIn(PLUGIN, plugin)
        self.assertIn(GGUF, plugin)
        self.assertIn("pip install --no-deps --require-hashes", plugin)
        self.assertIn("assert ops._CUDA_AVAILABLE", plugin)
        self.assertIn("import importlib.metadata as m, vllm, torch, gguf, vllm_gguf_plugin.ops as ops", plugin)
        self.assertNotRegex(text, r":latest\b")

    def test_the_pypi_build_pins_vllm_and_adds_the_plugin_after_its_layer_so_that_one_stays_cached(self):
        text = PYPI.read_text()
        self.assertRegex(text, r"(?m)^ARG VLLM_VERSION=\d+\.\d+\.\d+$")
        self.assertRegex(text, r"(?m)^ARG BASE_IMAGE=python:3\.\d+-slim$")
        self.assertRegex(text, r"(?m)^ARG LMCACHE_VERSION=$")
        steps = runs(text)
        vllm = next(i for i, r in enumerate(steps) if '"vllm==${VLLM_VERSION}"' in r)
        plugin = next(i for i, r in enumerate(steps) if "vllm-gguf-plugin" in r)
        self.assertLess(vllm, plugin, "the plugin goes in a later layer: vLLM's 6 GB one stays cached")
        self.assertNotIn("gguf", steps[vllm])
        self.assertIn("if [ -n \"${LMCACHE_VERSION}\" ]", steps[vllm])
        self.assertIn(PLUGIN, steps[plugin])
        self.assertIn(GGUF, steps[plugin])
        self.assertIn("/opt/vllm/bin/pip install --no-deps --require-hashes", steps[plugin])
        self.assertIn("assert ops._CUDA_AVAILABLE", steps[plugin])
        # No ARG between them: a new one would not change vLLM's layer, but none is needed.
        self.assertNotRegex(text.split('"vllm==${VLLM_VERSION}"', 1)[1].split("vllm-gguf-plugin", 1)[0], r"(?m)^ARG ")

    def test_both_install_the_rotary_plugin_for_llama_gguf_files_after_the_gguf_plugin_and_check_vllm_finds_it(self):
        for path, python in ((IMAGE, "python3"), (PYPI, "/opt/vllm/bin/python")):
            text = path.read_text()
            steps = runs(text)
            plugin = next(i for i, r in enumerate(steps) if "vllm-gguf-plugin" in r)
            [rope] = [i for i, r in enumerate(steps) if "arena_gguf_rope" in r]
            self.assertLess(plugin, rope, path.name)
            self.assertIn("COPY vllm-gguf-rope.py /tmp/arena_gguf_rope.py", text, path.name)
            self.assertIn("[vllm.general_plugins]\\narena_gguf_rope = arena_gguf_rope:register", steps[rope], path.name)
            self.assertIn(f"{python} -c \"import importlib.metadata as m; assert 'arena_gguf_rope' in", steps[rope], path.name)
            self.assertIn(f"site=\"$({python} -c", steps[rope], path.name)

    def test_both_run_the_supervisor_through_vllms_launcher_offline_and_copy_only_what_the_context_has(self):
        context = DEPLOY / "services"
        for path in (IMAGE, PYPI):
            text = path.read_text()
            self.assertIn('ENTRYPOINT ["/bin/sh", "/vllm/supervisor.sh"]', text, path.name)
            self.assertIn("HF_HUB_OFFLINE=1", text, path.name)
            self.assertIn("COPY vllm/supervisor.sh vllm/vllm-helper.py vllm/serve.py vllm/gguf_config.py /vllm/", text, path.name)
            for line in re.findall(r"(?m)^COPY (.+)$", text):
                for src in line.split()[:-1]:
                    self.assertTrue((context / src).exists(), src)
            # No secret in the image: the key reaches the servers by the environment at run time.
            self.assertNotRegex(text, r"(?i)(api[_-]?key|token|password)=", path.name)
            self.assertNotIn("docker.io", text, path.name)

    def test_compose_builds_arena_vllm_by_default_and_from_pypi_with_the_build_file(self):
        if shutil.which("docker") is None:
            self.skipTest("no docker here")
        plain = subprocess.run(compose() + ["config", "vllm"], capture_output=True, text=True, timeout=120)
        built = subprocess.run(compose("vllm-build.yml") + ["config", "vllm"], capture_output=True, text=True, timeout=120)
        self.assertEqual(0, plain.returncode, plain.stderr)
        self.assertEqual(0, built.returncode, built.stderr)
        self.assertIn("image: arena-vllm\n", plain.stdout)
        self.assertIn("dockerfile: vllm.Dockerfile", plain.stdout)
        self.assertIn("VLLM_IMAGE: vllm/vllm-openai:v0.31.0", plain.stdout)
        self.assertIn("VLLM_BIN: python3 /vllm/serve.py", plain.stdout)
        self.assertIn("image: arena-vllm\n", built.stdout)
        self.assertIn("dockerfile: vllm-pypi.Dockerfile", built.stdout)

    def test_the_fake_and_amd_run_another_image_and_build_nothing(self):
        if shutil.which("docker") is None:
            self.skipTest("no docker here")
        for overlay, image in (("fake-gpu.yml", "python:3.13-slim"), ("rocm.yml", "vllm/vllm-openai-rocm:v0.31.0")):
            out = subprocess.run(compose(overlay) + ["config", "vllm"], capture_output=True, text=True, timeout=120)
            self.assertEqual(0, out.returncode, out.stderr)
            self.assertIn(f"image: {image}\n", out.stdout, overlay)
            # A build there would tag the python or ROCm image with arena-vllm's contents.
            self.assertNotIn("build:", out.stdout, overlay)

    def test_the_release_builds_and_ships_it_as_its_own(self):
        """make-installer.sh's list of the release's own images (the services compose builds, every profile on), as it reads it."""
        if shutil.which("docker") is None:
            self.skipTest("no docker here")
        profiles = subprocess.run(compose(profiles="vllm") + ["config", "--profiles"], capture_output=True, text=True, timeout=120).stdout.split()
        out = subprocess.run(compose(profiles=",".join(p for p in profiles if p != "off")) + ["config"], capture_output=True, text=True, timeout=120)
        self.assertEqual(0, out.returncode, out.stderr)
        built = subprocess.run(["awk", """
  /^services:/ { s = 1; next }
  s && /^[^ ]/ { s = 0 }
  s && /^  [a-z0-9_-]+:[[:space:]]*$/ { svc = $1; sub(/:$/, "", svc) }
  s && /^    build:/ { print svc }"""], input=out.stdout, capture_output=True, text=True).stdout.split()
        self.assertIn("vllm", built)
        self.assertIn("app", built)
        self.assertNotIn("sglang", built)
        # The awk above is make-installer.sh's own.
        self.assertIn('s && /^    build:/ { print svc }', (DEPLOY / "scripts" / "make-installer.sh").read_text())


if __name__ == "__main__":
    unittest.main()
