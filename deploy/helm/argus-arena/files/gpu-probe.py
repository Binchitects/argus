#!/usr/bin/env python3
"""
What GPUs this machine has, for the app (Engine:HardwareFile), whatever their vendor: nvidia-smi for NVIDIA,
rocm-smi for AMD, and none at all (a machine that runs on the CPU). Python's standard library only, so it runs in
any engine image that has python3 (vLLM's does; the supervisor calls it every few seconds).

    gpu-probe.py [--out FILE]      # JSON on stdout, or written atomically to FILE

    {"at": 1760000000, "ram_bytes": N, "source": "nvidia-smi" | "rocm-smi" | "none",
     "gpus": [{"index": 0, "name": "...", "vendor": "nvidia", "backend": "cuda",
               "total_bytes": N, "free_bytes": N, "reserved_bytes": N}]}

GPU_PROBE_VENDOR=nvidia|amd|none skips the guessing. Never fails: a tool that is missing or says
something unexpected is passed over, and what is left is written.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import time

MIB = 1 << 20


def run(cmd: list[str]) -> str | None:
    if not shutil.which(cmd[0]):
        return None
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=15)
    except (OSError, subprocess.TimeoutExpired):
        return None
    return out.stdout if out.returncode == 0 else None


def nvidia() -> list[dict] | None:
    out = run(["nvidia-smi", "--query-gpu=index,name,memory.total,memory.free,memory.reserved", "--format=csv,noheader,nounits"])
    reserved = True
    if out is None:
        # Older drivers know no memory.reserved.
        out = run(["nvidia-smi", "--query-gpu=index,name,memory.total,memory.free", "--format=csv,noheader,nounits"])
        reserved = False
    if out is None:
        return None
    gpus = []
    for line in out.strip().splitlines():
        parts = [p.strip() for p in line.split(",")]
        try:
            gpu = {"index": int(parts[0]), "name": parts[1], "vendor": "nvidia", "backend": "cuda",
                   "total_bytes": int(float(parts[2])) * MIB, "free_bytes": int(float(parts[3])) * MIB,
                   "reserved_bytes": int(float(parts[4])) * MIB if reserved and len(parts) > 4 and parts[4].replace(".", "").isdigit() else 0}
        except (ValueError, IndexError):
            continue
        gpus.append(gpu)
    return gpus


def amd() -> list[dict] | None:
    out = run(["rocm-smi", "--showmeminfo", "vram", "--showproductname", "--json"])
    if out is None:
        return None
    try:
        cards = json.loads(out)
    except json.JSONDecodeError:
        return None
    gpus = []
    for key, card in sorted(cards.items()):
        if not key.startswith("card") or not isinstance(card, dict):
            continue
        try:
            index = int(key[4:])
            total = int(card.get("VRAM Total Memory (B)", 0))
            used = int(card.get("VRAM Total Used Memory (B)", 0))
        except ValueError:
            continue
        name = card.get("Card Series") or card.get("Card series") or card.get("Device Name") or card.get("Card model") or "AMD GPU"
        gpus.append({"index": index, "name": name, "vendor": "amd", "backend": "rocm",
                     "total_bytes": total, "free_bytes": max(0, total - used), "reserved_bytes": 0})
    return gpus


def ram_bytes() -> int:
    try:
        with open("/proc/meminfo") as f:
            for line in f:
                if line.startswith("MemTotal:"):
                    return int(line.split()[1]) * 1024
    except OSError:
        pass
    return 0


def probe() -> dict:
    vendor = os.environ.get("GPU_PROBE_VENDOR", "").lower()
    gpus, source = [], "none"
    for name, read in (("nvidia", nvidia), ("amd", amd)):
        if vendor and vendor != name:
            continue
        found = read()
        if found:
            gpus, source = found, "nvidia-smi" if name == "nvidia" else "rocm-smi"
            break
    return {"at": int(time.time()), "ram_bytes": ram_bytes(), "source": source, "gpus": gpus}


def main(argv: list[str]) -> int:
    text = json.dumps(probe())
    if len(argv) >= 2 and argv[0] == "--out":
        tmp = argv[1] + ".tmp"
        with open(tmp, "w") as f:
            f.write(text + "\n")
        os.replace(tmp, argv[1])
    else:
        print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
