"""
deploy/services/gpu-probe.py: what GPUs a machine has, for any vendor, with fake nvidia-smi and rocm-smi on PATH:
NVIDIA cards of different sizes, AMD cards, a driver without memory.reserved, and a machine with no GPU at all.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

PROBE = Path(__file__).resolve().parents[2] / "deploy" / "services" / "gpu-probe.py"
MIB = 1 << 20


class GpuProbeTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="gpu-probe-"))
        self.bin = self.dir / "bin"
        self.bin.mkdir()
        # Only the fakes and cat: the host's own nvidia-smi or rocm-smi must not answer.
        (self.bin / "cat").symlink_to(shutil.which("cat"))
        self.addCleanup(shutil.rmtree, self.dir, True)

    def tool(self, name: str, script: str):
        p = self.bin / name
        p.write_text("#!/bin/sh\n" + script)
        p.chmod(0o755)

    def probe(self, *args: str, env: dict | None = None) -> dict:
        e = {"PATH": str(self.bin)} | (env or {})
        out = subprocess.run([sys.executable, str(PROBE), *args], env=e, capture_output=True, text=True, timeout=30)
        self.assertEqual(0, out.returncode, out.stderr)
        return json.loads(out.stdout) if out.stdout.strip() else {}

    def test_nvidia_cards_of_different_sizes_are_listed_by_index_with_their_memory(self):
        self.tool("nvidia-smi", 'echo "0, NVIDIA GeForce RTX 3090, 24576, 3000, 300"\necho "1, NVIDIA GeForce RTX 3060, 12288, 12000, 200"\n')
        h = self.probe()
        self.assertEqual("nvidia-smi", h["source"])
        self.assertEqual([0, 1], [g["index"] for g in h["gpus"]])
        self.assertEqual(("nvidia", "cuda"), (h["gpus"][0]["vendor"], h["gpus"][0]["backend"]))
        self.assertEqual(24576 * MIB, h["gpus"][0]["total_bytes"])
        self.assertEqual(3000 * MIB, h["gpus"][0]["free_bytes"])
        self.assertEqual(200 * MIB, h["gpus"][1]["reserved_bytes"])
        self.assertGreater(h["ram_bytes"], 0)

    def test_a_driver_without_memory_reserved_is_asked_again_without_it(self):
        self.tool("nvidia-smi", 'case "$*" in *reserved*) echo "Field memory.reserved is not a valid field" >&2; exit 2;; esac\n'
                                'echo "0, NVIDIA A100-SXM4-80GB, 81920, 80000"\n')
        h = self.probe()
        self.assertEqual(1, len(h["gpus"]))
        self.assertEqual(0, h["gpus"][0]["reserved_bytes"])

    def test_amd_cards_come_from_rocm_smi(self):
        cards = {"card0": {"Card Series": "AMD Instinct MI300X", "VRAM Total Memory (B)": str(192 * 1024 * MIB), "VRAM Total Used Memory (B)": str(1024 * MIB)},
                 "card1": {"Card Series": "AMD Instinct MI300X", "VRAM Total Memory (B)": str(192 * 1024 * MIB), "VRAM Total Used Memory (B)": "0"}}
        self.tool("rocm-smi", f"cat <<'EOF'\n{json.dumps(cards)}\nEOF\n")
        h = self.probe()
        self.assertEqual("rocm-smi", h["source"])
        self.assertEqual([("amd", "rocm", 0), ("amd", "rocm", 1)], [(g["vendor"], g["backend"], g["index"]) for g in h["gpus"]])
        self.assertEqual(191 * 1024 * MIB, h["gpus"][0]["free_bytes"])

    def test_no_gpu_tool_is_a_machine_without_gpus_and_the_file_is_written_whole(self):
        out = self.dir / "hardware.json"
        self.probe("--out", str(out))
        h = json.loads(out.read_text())
        self.assertEqual(("none", []), (h["source"], h["gpus"]))
        self.assertFalse((self.dir / "hardware.json.tmp").exists())

    def test_the_vendor_can_be_named_and_a_broken_tool_is_passed_over(self):
        self.tool("nvidia-smi", "echo garbage\n")
        self.tool("rocm-smi", "echo '{\"card0\": {\"VRAM Total Memory (B)\": \"1000\", \"VRAM Total Used Memory (B)\": \"0\"}}'\n")
        self.assertEqual("rocm-smi", self.probe()["source"])
        self.assertEqual("none", self.probe(env={"GPU_PROBE_VENDOR": "nvidia"})["source"])


if __name__ == "__main__":
    unittest.main()
