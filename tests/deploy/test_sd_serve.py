"""
deploy/services/sd-serve.sh: the picture and video servers' script, run by /bin/sh with a fake sd-server and nvidia-smi.
It reads its control file line by line (never as commands), starts the server only for a new generation, never again on
its own after it exits, waits for missing files, decodes on the GPU only when it has room, and reports what it does.
"""
from __future__ import annotations

import json
import os
import shutil
import signal
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "deploy" / "services" / "sd-serve.sh"


class SdServeTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="sd-serve-"))
        for d in ("control", "status", "models", "bin"):
            (self.dir / d).mkdir()
        # The fake server: writes its arguments and CUDA_VISIBLE_DEVICES, then runs until killed (or exits with FAKE_EXIT).
        server = self.dir / "bin" / "sd-server"
        server.write_text(
            '#!/bin/sh\n'
            'n=$(cat "$FAKE_DIR/starts" 2>/dev/null || echo 0); echo $((n + 1)) > "$FAKE_DIR/starts"\n'
            'printf "%s\\n" "$@" > "$FAKE_DIR/args"\n'
            'echo "${CUDA_VISIBLE_DEVICES:-unset}" > "$FAKE_DIR/gpu"\n'
            'pwd > "$FAKE_DIR/cwd"\n'
            'echo "loading model"\n'
            '[ -f "$FAKE_DIR/exit" ] && exit "$(cat "$FAKE_DIR/exit")"\n'
            # Stopped, it gives its GPU memory back (free_after, when the test says how much is free then).
            'trap \'[ -f "$FAKE_DIR/free_after" ] && cp "$FAKE_DIR/free_after" "$FAKE_DIR/free"; exit 0\' TERM\n'
            'while true; do sleep 0.1; done\n')
        smi = self.dir / "bin" / "nvidia-smi"
        smi.write_text('#!/bin/sh\ncat "$FAKE_DIR/free" 2>/dev/null || exit 9\n')
        for f in (server, smi):
            f.chmod(0o755)
        (self.dir / "free").write_text("20000\n")
        self.proc: subprocess.Popen | None = None

    def tearDown(self):
        if self.proc and self.proc.poll() is None:
            self.proc.send_signal(signal.SIGTERM)
            try:
                self.proc.wait(timeout=20)
            except subprocess.TimeoutExpired:
                self.proc.kill()
        shutil.rmtree(self.dir, ignore_errors=True)

    def run_script(self):
        # The script's paths, under this test's folder: /control, /status and /models are its arguments' roots.
        text = SCRIPT.read_text()
        for root in ("/control/", "/status/", "/models"):
            text = text.replace(root, str(self.dir / root.strip("/")) + ("/" if root.endswith("/") else ""))
        script = self.dir / "sd-serve.sh"
        script.write_text(text)
        env = {"PATH": f"{self.dir / 'bin'}:/usr/bin:/bin", "NAME": "imagegen", "SD_SERVER": str(self.dir / "bin" / "sd-server"),
               "SD_SERVE_TICK": "0.2", "FAKE_DIR": str(self.dir)}
        self.proc = subprocess.Popen(["/bin/sh", str(script)], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def conf(self, *lines: str):
        p = self.dir / "control" / "imagegen.conf"
        p.write_text("\n".join(lines) + "\n")

    def status(self) -> dict:
        p = self.dir / "status" / "imagegen.json"
        return json.loads(p.read_text()) if p.exists() else {}

    def until(self, check, what: str, seconds: float = 10):
        end = time.time() + seconds
        while time.time() < end:
            if check():
                return
            time.sleep(0.1)
        self.fail(f"not within {seconds} s: {what} (status {self.status()})")

    def starts(self) -> int:
        p = self.dir / "starts"
        return int(p.read_text()) if p.exists() else 0

    def model(self, *files: str):
        for f in files:
            (self.dir / "models" / f).parent.mkdir(parents=True, exist_ok=True)
            (self.dir / "models" / f).write_text("weights")

    def test_A_new_generation_starts_the_server_with_its_arguments_read_as_text_and_on_its_gpu(self):
        self.model("image/a.gguf")
        self.conf("gen=1", "want=on", "model=A", "gpu=1", "file=image/a.gguf", "arg=--diffusion-model", "arg=image/a.gguf",
                  "arg=--negative-prompt", "arg=blurry, $(touch pwned) `id`", "arg=--steps", "arg=4")
        self.run_script()
        self.until(lambda: self.status().get("phase") == "running" and (self.dir / "args").exists(), "it runs")
        args = (self.dir / "args").read_text().splitlines()
        self.assertEqual(args[:4], ["--listen-ip", "0.0.0.0", "--listen-port", "1234"])
        self.assertEqual(args[4:], ["--diffusion-model", "image/a.gguf", "--negative-prompt", "blurry, $(touch pwned) `id`", "--steps", "4"])
        self.assertFalse((self.dir / "pwned").exists())
        self.assertEqual((self.dir / "gpu").read_text().strip(), "1")
        self.assertEqual((self.dir / "cwd").read_text().strip(), str(self.dir / "models"))
        self.assertEqual(self.status()["gen"], "1")
        self.assertEqual(self.status()["model"], "A")
        self.until(lambda: "loading model" in (self.dir / "status" / "imagegen.log").read_text(), "its output is in its log")
        # The same generation: it is not started again; off stops it.
        time.sleep(0.6)
        self.assertEqual(self.starts(), 1)
        self.conf("gen=1", "want=off", "model=A")
        self.until(lambda: self.status().get("phase") == "off", "off stops it")
        # On again, the same generation: it starts again.
        self.conf("gen=1", "want=on", "model=A", "file=image/a.gguf")
        self.until(lambda: self.status().get("phase") == "running" and self.starts() == 2, "on again starts it again")

    def test_It_waits_for_its_files_and_starts_once_they_are_there(self):
        self.conf("gen=1", "want=on", "model=A", "file=image/a.gguf", "file=image/vae.safetensors", "arg=--vae", "arg=image/vae.safetensors")
        self.run_script()
        self.until(lambda: self.status().get("phase") == "waiting", "it waits")
        self.assertEqual(self.status()["missing"], ["image/a.gguf", "image/vae.safetensors"])
        self.assertEqual(self.starts(), 0)
        self.model("image/a.gguf", "image/vae.safetensors")
        self.until(lambda: self.status().get("phase") == "running", "it starts once its files are there")

    def test_A_server_that_exits_is_not_started_again_until_the_app_raises_its_generation(self):
        self.model("image/a.gguf")
        (self.dir / "exit").write_text("3")
        self.conf("gen=4", "want=on", "model=A", "file=image/a.gguf")
        self.run_script()
        self.until(lambda: self.status().get("phase") == "exited", "it exited")
        self.assertEqual(self.status()["exit"], 3)
        time.sleep(0.8)
        self.assertEqual(self.starts(), 1)
        (self.dir / "exit").unlink()
        self.conf("gen=5", "want=on", "model=A", "file=image/a.gguf")
        self.until(lambda: self.status().get("phase") == "running" and self.status().get("gen") == "5", "a new generation starts it again")
        self.assertEqual(self.starts(), 2)

    def test_It_decodes_on_the_gpu_only_with_the_room_it_needs_and_says_where(self):
        self.model("video/w.gguf")
        lines = ["want=on", "model=W", "file=video/w.gguf", "arg=--max-vram", "arg=3", "vae_gpu_mb=13312",
                 "vae_gpu_arg=--max-vram", "vae_gpu_arg=12", "vae_gpu_arg=--offload-to-cpu", "vae_cpu_arg=--vae-on-cpu"]
        (self.dir / "free").write_text("2600\n")
        self.conf("gen=1", *lines)
        self.run_script()
        self.until(lambda: self.status().get("phase") == "running" and (self.dir / "args").exists(), "it runs")
        self.assertEqual(self.status()["vae"], "cpu")
        self.assertEqual((self.dir / "args").read_text().splitlines()[4:], ["--max-vram", "3", "--vae-on-cpu"])
        # Room now: the next generation decodes on the GPU, with its budget then.
        (self.dir / "free").write_text("20000\n")
        (self.dir / "args").unlink()
        self.conf("gen=2", *lines)
        self.until(lambda: self.status().get("gen") == "2" and (self.dir / "args").exists(), "the new generation runs")
        self.assertEqual(self.status()["vae"], "gpu")
        self.assertEqual((self.dir / "args").read_text().splitlines()[4:], ["--max-vram", "3", "--max-vram", "12", "--offload-to-cpu"])
        # The GPU cannot be read: on the CPU, which always fits.
        (self.dir / "free").unlink()
        (self.dir / "args").unlink()
        self.conf("gen=3", *lines)
        self.until(lambda: self.status().get("gen") == "3" and (self.dir / "args").exists(), "the third runs")
        self.assertEqual(self.status()["vae"], "cpu")
        self.assertIsNone(self.status()["free_mb"])

    def test_Started_again_it_decides_where_it_decodes_by_the_memory_free_once_it_has_stopped(self):
        # The app made room (unloaded the chat models) and raised gen: the memory the old server and the chat models held is
        # free only once the old server has stopped, which is after the script read the GPU on that round.
        self.model("video/w.gguf")
        lines = ["want=on", "model=W", "file=video/w.gguf", "vae_gpu_mb=13312", "vae_gpu_arg=--max-vram", "vae_gpu_arg=12",
                 "vae_gpu_arg=--offload-to-cpu", "vae_cpu_arg=--vae-on-cpu"]
        (self.dir / "free").write_text("2600\n")
        self.conf("gen=1", *lines)
        self.run_script()
        self.until(lambda: self.status().get("phase") == "running" and self.status().get("vae") == "cpu", "it runs, decoding on the CPU")
        (self.dir / "free_after").write_text("20000\n")
        (self.dir / "args").unlink()
        self.conf("gen=2", *lines)
        self.until(lambda: self.status().get("gen") == "2" and (self.dir / "args").exists(), "the new generation runs")
        self.assertEqual(self.status()["vae"], "gpu")
        self.assertEqual((self.dir / "args").read_text().splitlines()[4:], ["--max-vram", "12", "--offload-to-cpu"])

    def test_It_stops_its_server_when_it_is_stopped(self):
        self.model("image/a.gguf")
        self.conf("gen=1", "want=on", "model=A", "file=image/a.gguf")
        self.run_script()
        self.until(lambda: self.status().get("phase") == "running", "it runs")
        self.proc.send_signal(signal.SIGTERM)
        self.proc.wait(timeout=20)
        self.assertEqual(self.status()["phase"], "off")
        out = subprocess.run(["pgrep", "-f", str(self.dir / "bin" / "sd-server")], capture_output=True, text=True).stdout.strip()
        self.assertEqual(out, "")


if __name__ == "__main__":
    unittest.main()
