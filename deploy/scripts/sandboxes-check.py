#!/usr/bin/env python3
"""Every sandbox of the stack that is on, checked for real against the running containers:
    python3 scripts/sandboxes-check.py
- the code sandbox (profile sandbox): scripts/sandbox-check.py, its jobs, isolation and limits;
- the 3D sandbox (profile 3d): scripts/blender-check.py, a model and the 3D tool's own script, its isolation and limits;
- the proof checker (profile lean): scripts/lean-check.py, Mathlib and its limits;
- the browser agent's browser (profile browser), Code Studio's workspaces (workspaces), the media studio's models (studio)
  and Laya (laya): running, healthy where they have a health check, and answering where they serve.
A sandbox whose feature is off is said and skipped. Exit status: the sandboxes that failed.
"""
import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

# name: (its container, the check that runs jobs through it, or None)
SANDBOXES = {
    "code sandbox": ("sandbox", "sandbox-check.py"),
    "3D sandbox": ("blender", "blender-check.py"),
    "proof checker (Lean)": ("lean", "lean-check.py"),
    "browser agent's browser": ("browser", None),
    "Code Studio's workspaces": ("workspaces", None),
    "media studio's models": ("studio", None),
    "Laya": ("laya", None),
}


def state(container):
    """The container's state and health ("running", "healthy"), or None when there is none."""
    p = subprocess.run(["docker", "inspect", container, "--format", "{{json .State}}"], capture_output=True, text=True, check=False)
    if p.returncode != 0:
        return None
    s = json.loads(p.stdout)
    return s.get("Status"), (s.get("Health") or {}).get("Status"), s.get("RestartCount", 0)


def main():
    failed, ran = [], 0
    for name, (container, script) in SANDBOXES.items():
        now = state(container)
        if now is None:
            print(f"== {name}: off (no {container} container)")
            continue
        status, health, _ = now
        ran += 1
        print(f"== {name}: {status}{f', {health}' if health else ''}")
        if status != "running" or health in ("unhealthy", "starting"):
            failed.append(name)
            continue
        if script:
            if subprocess.run([sys.executable, str(HERE / script)], check=False).returncode != 0:
                failed.append(name)
    print()
    print(f"{ran - len(failed)} of {ran} sandboxes on are well" + (f"; failed: {', '.join(failed)}" if failed else ""))
    return len(failed)


if __name__ == "__main__":
    sys.exit(main())
