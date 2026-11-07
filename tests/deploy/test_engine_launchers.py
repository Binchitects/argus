"""
The launchers of the LMDeploy and Strata engines (deploy/services/lmdeploy/serve.py, deploy/services/strata/serve.py), and
arena-lmdeploy's image (services/lmdeploy.Dockerfile, lmdeploy-build.yml), read and run without a GPU: the supervisor's
names in each engine's own words, the key handed over inside the process (never on a command line), Strata given a split
model's folder and nothing to download, and the image pinned.
"""
from __future__ import annotations

import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

from support import REPO

SERVICES = REPO / "deploy" / "services"
FAKE = REPO / "tools" / "fake-vllm"


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class LmdeployLauncherTests(unittest.TestCase):
    def test_the_supervisors_names_are_lmdeploys_and_the_key_goes_inside_the_process(self):
        serve = load("lmdeploy_serve", SERVICES / "lmdeploy" / "serve.py")
        self.assertEqual(["/library/hf/a@b", "--tp", "2", "--model-name", "turbo", "--server-name", "0.0.0.0", "--server-port", "8001"],
                         serve.args(["/library/hf/a@b", "--tp", "2", "--served-model-name", "turbo", "--host", "0.0.0.0", "--port", "8001"]))
        text = (SERVICES / "lmdeploy" / "serve.py").read_text()
        self.assertIn('key = os.environ.pop("VLLM_API_KEY", "")', text)
        self.assertIn('sys.argv = ["lmdeploy", "serve", "api_server", *run, *(["--api-keys", key] if key else [])]', text)

    def test_the_fake_lmdeploy_serves_with_the_key_through_the_real_launcher(self):
        port = 18000 + os.getpid() % 1000
        env = dict(os.environ, VLLM_API_KEY="the-engine-key", PYTHONPATH=str(FAKE / "lmdeploy-module"))
        proc = subprocess.Popen([sys.executable, str(SERVICES / "lmdeploy" / "serve.py"), "/library/hf/a@b", "--session-len", "4096",
                                 "--tool-call-parser", "qwen2d5", "--served-model-name", "turbo", "--host", "127.0.0.1", "--port", str(port)], env=env,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        try:
            import urllib.error
            import urllib.request
            for _ in range(100):
                try:
                    with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1):
                        break
                except OSError:
                    time.sleep(0.1)
            with self.assertRaises(urllib.error.HTTPError) as refused:
                urllib.request.urlopen(f"http://127.0.0.1:{port}/v1/models", timeout=2)
            self.assertEqual(401, refused.exception.code)
            refused.exception.close()
            ask = urllib.request.Request(f"http://127.0.0.1:{port}/v1/models", headers={"Authorization": "Bearer the-engine-key"})
            with urllib.request.urlopen(ask, timeout=2) as r:
                self.assertEqual("turbo", json.load(r)["data"][0]["id"])
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/metrics", timeout=2) as r:
                self.assertIn("lmdeploy:num_requests_running", r.read().decode())
            # Its tool-call parser as the app writes it reaches LMDeploy: a request with tools gets tool_calls, not its refusal.
            tools = [{"type": "function", "function": {"name": "find_symbol", "parameters": {"type": "object"}}}]
            ask = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions", method="POST",
                                         headers={"Authorization": "Bearer the-engine-key", "Content-Type": "application/json"},
                                         data=json.dumps({"model": "turbo", "messages": [{"role": "user", "content": "find"}], "tools": tools}).encode())
            with urllib.request.urlopen(ask, timeout=5) as r:
                self.assertEqual("tool_calls", json.load(r)["choices"][0]["finish_reason"])
            # Its command line names no key.
            self.assertNotIn("the-engine-key", Path(f"/proc/{proc.pid}/cmdline").read_bytes().decode(errors="replace"))
        finally:
            proc.terminate()
            proc.wait(10)
            proc.stdout.close()


class SupervisedEnginesTests(unittest.TestCase):
    def test_an_engine_run_by_the_supervisor_from_an_image_not_ours_has_no_check_of_the_images(self):
        """The supervisor starts a server per model the app wants and none while it wants none: an image's own
        HEALTHCHECK (Strata's waits two hours for a server of its own) keeps such a service "starting" and the installer
        waiting until it gives up. A supervised engine on an image the stack does not build switches it off."""
        import yaml
        compose = yaml.safe_load((REPO / "deploy" / "docker-compose.yml").read_text())
        wrong = []
        for name, service in compose["services"].items():
            entry = " ".join(map(str, service.get("entrypoint") or []))
            # ${STRATA_IMAGE:-ghcr.io/...}: the image named by default.
            image = re.sub(r"^\$\{[A-Z_]+:-(.*)\}$", r"\1", str(service.get("image", "")))
            if "/vllm/supervisor.sh" in entry and not image.startswith("arena-") and "lmsysorg/sglang" not in image:
                if (service.get("healthcheck") or {}).get("disable") is not True:
                    wrong.append(name)
        self.assertEqual([], wrong, "a supervised engine whose image may carry its own HEALTHCHECK keeps it on")


class StrataLauncherTests(unittest.TestCase):
    def setUp(self):
        self.serve = load("strata_serve", SERVICES / "strata" / "serve.py")
        self.dir = Path(tempfile.mkdtemp())

    def model(self, *files: str) -> str:
        for f in files:
            (self.dir / f).write_bytes(b"GGUF")
        return str(self.dir / files[0])

    def test_a_split_model_is_named_by_its_folder_and_nothing_is_downloaded(self):
        first = self.model("Flash-UD-IQ4_XS-00001-of-00003.gguf", "mtp-Flash-Q8_0.gguf")
        args, name, port = self.serve.launch_args([first, "--context", "32768", "--parallel", "2", "--kv", "int8", "--served-model-name", "flash",
                                                   "--host", "0.0.0.0", "--port", "8001"], "1,2", [])
        def after(flag):
            return args[args.index(flag) + 1]
        self.assertEqual(("flash", 8001), (name, port))
        # Strata's draft layers are its own downloads: none, as the real one's plan says with --mtp off --drafting off.
        self.assertEqual((str(self.dir), "8001", "1,2", "off", "off", "off", "flash"),
                         (after("--model-path"), after("--port"), after("--gpu"), after("--mtp"), after("--drafting"), after("--vision"), after("--save-preset")))
        self.assertEqual(("32768", "2", "int8"), (after("--context"), after("--parallel"), after("--kv")))
        # The key by the environment's name, never itself; the supervisor's own flags are not Strata's.
        self.assertEqual("STRATA_LAUNCH_KEY", after("--api-key-env"))
        self.assertNotIn("--host", args)
        self.assertNotIn("--served-model-name", args)
        self.assertNotIn("--preset", args)
        # Saved once: its own preset again.
        self.assertEqual("flash", self.serve.launch_args([first, "--served-model-name", "flash", "--port", "8001"], None, ["flash"])[0][3])
        # A drafter an admin put in its data folder, by name.
        os.environ["STRATA_DRAFTING"] = "mtp"
        try:
            args = self.serve.launch_args([first, "--served-model-name", "flash", "--port", "8001"], None, [])[0]
            self.assertEqual("mtp", after("--drafting"))
            self.assertNotIn("--mtp", args)
        finally:
            del os.environ["STRATA_DRAFTING"]

    def test_one_file_is_named_itself_and_a_file_that_is_not_gguf_is_refused(self):
        one = self.model("Small-Q4_K_M.gguf")
        args, _, _ = self.serve.launch_args([one, "--served-model-name", "s", "--port", "8002"], None, [])
        self.assertEqual(one, args[args.index("--model-path") + 1])
        self.assertNotIn("--gpu", args)
        with self.assertRaises(ValueError):
            self.serve.launch_args(["/library/hf/a@b", "--served-model-name", "s", "--port", "8002"], None, [])

    def test_the_fake_strata_serves_through_the_real_launcher_and_a_term_stops_it(self):
        model = self.model("Flash-00001-of-00002.gguf")
        port = 19000 + os.getpid() % 1000
        state = self.dir / "state"
        env = dict(os.environ, VLLM_API_KEY="the-engine-key", STRATA_CLI=str(FAKE / "strata"), FAKE_STRATA_STATE=str(state),
                   STRATA_DATA=str(self.dir / "data"), CUDA_VISIBLE_DEVICES="0")
        # Strata's folders under /data are made by the launcher: here, a writable stand-in.
        script = (SERVICES / "strata" / "serve.py").read_text().replace('"/data/', f'"{self.dir}/data/')
        launcher = self.dir / "serve.py"
        launcher.write_text(script)
        proc = subprocess.Popen([sys.executable, str(launcher), model, "--served-model-name", "flash", "--host", "0.0.0.0", "--port", str(port)],
                                env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        try:
            import urllib.request
            ask = urllib.request.Request(f"http://127.0.0.1:{port}/v1/models", headers={"Authorization": "Bearer the-engine-key"})
            for _ in range(150):
                try:
                    with urllib.request.urlopen(ask, timeout=1) as r:
                        self.assertEqual("flash", json.load(r)["data"][0]["id"])
                        break
                except OSError:
                    time.sleep(0.1)
            else:
                self.fail(proc.stdout.read() if proc.poll() is not None else "it never served")
            self.assertTrue((state / "flash.preset").exists())
            for pid in [p for p in os.listdir("/proc") if p.isdigit()]:
                try:
                    self.assertNotIn("the-engine-key", Path(f"/proc/{pid}/cmdline").read_bytes().decode(errors="replace"))
                except OSError:
                    pass
        finally:
            proc.terminate()
            self.assertEqual(0, proc.wait(30))
            proc.stdout.close()
        self.assertEqual([], [f for f in os.listdir(state) if f.endswith(".server")])


class LmdeployImageTests(unittest.TestCase):
    def test_lmdeploy_and_its_base_are_pinned_and_it_runs_the_supervisor_offline(self):
        text = (SERVICES / "lmdeploy.Dockerfile").read_text()
        self.assertRegex(text, r"(?m)^ARG LMDEPLOY_VERSION=\d+\.\d+\.\d+$")
        self.assertRegex(text, r"(?m)^ARG BASE_IMAGE=python:3\.\d+-slim$")
        self.assertIn('"lmdeploy==${LMDEPLOY_VERSION}"', text)
        self.assertIn('ENTRYPOINT ["/bin/sh", "/vllm/supervisor.sh"]', text)
        self.assertIn("ENGINE_SERVER=lmdeploy", text)
        self.assertIn("HF_HUB_OFFLINE=1", text)
        for line in re.findall(r"(?m)^COPY (.+)$", text):
            for src in line.split()[:-1]:
                self.assertTrue((SERVICES / src).exists(), src)
        build = (REPO / "deploy" / "lmdeploy-build.yml").read_text()
        self.assertIn("dockerfile: lmdeploy.Dockerfile", build)
        self.assertIn("image: arena-lmdeploy:${LMDEPLOY_VERSION:-0.18.0}", build)

    def test_a_build_host_names_its_mirrors_and_apt_gives_up_in_seconds_not_minutes(self):
        text = (SERVICES / "lmdeploy.Dockerfile").read_text()
        self.assertRegex(text, r"(?m)^ARG APT_MIRROR=$")
        self.assertRegex(text, r"(?m)^ARG PIP_INDEX_URL=$")
        self.assertIn("Acquire::http::Timeout=30", text)
        self.assertIn('${PIP_INDEX_URL:+--index-url "$PIP_INDEX_URL"}', text)
        # A base with a compiler skips apt altogether.
        self.assertIn("if command -v gcc >/dev/null && command -v ps >/dev/null; then exit 0; fi", text)
        build = (REPO / "deploy" / "lmdeploy-build.yml").read_text()
        self.assertIn("APT_MIRROR: ${LMDEPLOY_APT_MIRROR:-}", build)
        self.assertIn("PIP_INDEX_URL: ${LMDEPLOY_PIP_INDEX:-}", build)
        # The official addresses are the default; no other mirror is named, as a default or a suggestion.
        docs = (REPO / "docs" / "deployment.md").read_text()
        for where in (text, build, docs):
            self.assertNotRegex(where, r"(?i)https?://(?!deb\.debian\.org|pypi\.org|<)[^\s`]*(mirror|debian|pypi)")


if __name__ == "__main__":
    unittest.main()
