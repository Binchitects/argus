#!/usr/bin/env python3
"""
Strata's server as the supervisor starts it (services/vllm/supervisor.sh, ENGINE_SERVER=strata), in Strata's own image
(ghcr.io/niko1221/strata), with its Python. Strata starts its server through `strata launch`, which prepares the model
and returns once it serves; this stays in the foreground for the supervisor while it serves, and stops it on SIGTERM.

    serve.py FILE --context N --parallel N --kv int8 --served-model-name NAME --host HOST --port PORT

- FILE is a GGUF file of the library: a split one's folder is named instead of its first part, as Strata takes it.
- Nothing is ever downloaded (the host may have no network): Strata's draft layers (its MTP pack, 5.2 GB, and DFlash2's
  drafter) are its own downloads, so it starts without drafting (--mtp off --drafting off), unless STRATA_DRAFTING names
  one (mtp, dflash2) whose files an admin has put in its data folder. Pictures are off.
- The GPUs are the supervisor's CUDA_VISIBLE_DEVICES, given to Strata as its --gpu (nvidia-smi's numbers) and taken out of
  the environment, so Strata does not number them again.
- The key (VLLM_API_KEY, from the supervisor) goes to Strata by the environment (--api-key-env), never on a command line.
- Each model is a preset named after it, in Strata's data folder (/data, a volume): a second start of the same settings
  prepares nothing again.

Every 10 seconds it asks `strata launch --status` whether its port still serves; three times not: it ends (code 1), and
the supervisor starts it again later.
"""
from __future__ import annotations

import json
import os
import re
import signal
import subprocess
import sys
import time

STRATA = os.environ.get("STRATA_CLI", "/opt/strata/terminal/bin/strata")
DATA = os.environ.get("STRATA_DATA", "/data/Strata-data")
SPLIT = re.compile(r"-\d{5}-of-\d{5}\.gguf$", re.IGNORECASE)


def value(argv: list[str], flag: str) -> str | None:
    return argv[argv.index(flag) + 1] if flag in argv[:-1] else None


def without(argv: list[str], *flags: str) -> list[str]:
    out, skip = [], False
    for a in argv:
        if skip:
            skip = False
        elif a in flags:
            skip = True
        else:
            out.append(a)
    return out


def presets() -> list[str]:
    """The presets Strata has saved (in /data)."""
    try:
        out = subprocess.run([STRATA, "launch", "--list-presets", "--output", "json"], capture_output=True, text=True, timeout=60).stdout
        return [p.get("name") for p in json.loads(out or "{}").get("presets") or [] if isinstance(p, dict)]
    except (OSError, subprocess.TimeoutExpired, json.JSONDecodeError, AttributeError):
        return []


def launch_args(argv: list[str], gpus: str | None, saved: list[str] | None = None) -> tuple[list[str], str, int]:
    """`strata launch`'s arguments for the supervisor's, its preset's name and its port; a ValueError says why not."""
    if not argv or argv[0].startswith("--"):
        raise ValueError("the first argument is the model's GGUF file")
    path, rest = argv[0], argv[1:]
    name = value(rest, "--served-model-name")
    port = value(rest, "--port")
    if not name or not port or not port.isdigit():
        raise ValueError("--served-model-name and --port are the supervisor's, and both are needed")
    if not path.lower().endswith(".gguf"):
        raise ValueError(f"Strata runs GGUF files, and {path} is not one")
    model = os.path.dirname(path) if SPLIT.search(path) else path
    drafting = os.environ.get("STRATA_DRAFTING", "").strip().lower()
    out = ["launch", "--no-tui", "--yes", "--model-path", model, "--data-dir", DATA, "--access", "lan", "--port", port,
           "--vision", "off", "--tune", "skip", "--save-preset", name, "--api-key-env", "STRATA_LAUNCH_KEY"]
    # Its own preset again once it has one (what was prepared for it is kept); the flags after it are this start's.
    if name in (saved or []):
        out[2:2] = ["--preset", name]
    out += ["--drafting", drafting] if drafting in ("mtp", "dflash2") else ["--mtp", "off", "--drafting", "off"]
    if gpus:
        out += ["--gpu", gpus]
    out += without(rest, "--served-model-name", "--host", "--port")
    return out, name, int(port)


def serving(port: int) -> bool:
    """Whether `strata launch --status` lists a server on this port that is ready."""
    try:
        out = subprocess.run([STRATA, "launch", "--status", "--output", "json"], capture_output=True, text=True, timeout=30).stdout
        running = json.loads(out or "{}").get("running") or []
    except (OSError, subprocess.TimeoutExpired, json.JSONDecodeError, AttributeError):
        return False
    return any(isinstance(r, dict) and str(r.get("port")) == str(port) and r.get("state") == "ready" for r in running)


def main(argv: list[str]) -> int:
    gpus = os.environ.pop("CUDA_VISIBLE_DEVICES", None)
    os.environ.pop("HIP_VISIBLE_DEVICES", None)
    try:
        args, name, port = launch_args(argv, gpus, presets())
    except ValueError as e:
        print(f"serve.py: {e}", file=sys.stderr, flush=True)
        return 1
    key = os.environ.pop("VLLM_API_KEY", "")
    env = dict(os.environ, STRATA_LAUNCH_KEY=key)
    for d in ("/data/state", "/data/home", "/data/install/third_party"):
        os.makedirs(d, exist_ok=True)
    # llama.cpp at the image's pinned commit, so the first start fetches nothing from GitHub (as the image's entrypoint does).
    seed, link = "/opt/strata/seed/third_party/llama.cpp", "/data/install/third_party/llama.cpp"
    if os.path.isdir(seed) and not os.path.lexists(link):
        os.symlink(seed, link)
    stopping = False

    def stop(*_):
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    print(f"serve.py: strata {' '.join(args)}", flush=True)
    started = subprocess.Popen([STRATA, *args], env=env)
    while started.poll() is None and not stopping:
        time.sleep(1)
    if stopping or started.returncode != 0:
        if started.poll() is None:
            started.send_signal(signal.SIGINT)
            started.wait(60)
        subprocess.run([STRATA, "launch", "--stop", "--preset", name], timeout=180, check=False)
        return 0 if stopping else (started.returncode or 1)
    missed = 0
    while not stopping:
        for _ in range(10):
            if stopping:
                break
            time.sleep(1)
        missed = 0 if serving(port) else missed + 1
        if missed >= 3:
            print(f"serve.py: {name} no longer serves on port {port}", file=sys.stderr, flush=True)
            subprocess.run([STRATA, "launch", "--stop", "--preset", name], timeout=180, check=False)
            return 1
    # Drained by Strata: it ends the answers it has, unloads, and frees the GPUs.
    subprocess.run([STRATA, "launch", "--stop", "--preset", name], timeout=180, check=False)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
