"""
A throwaway copy of deploy/ for the script tests: the scripts under test, a few
files around them, and fake docker and podman commands on PATH that log every
call. Nothing here needs a container engine or the network.
"""
from __future__ import annotations

import json
import os
import shutil
import stat
import subprocess
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SCRIPTS = ["airgap.sh", "backup.sh", "test-project.sh", "restore-test.sh", "rollback-test.sh", "recovery-check.py"]
COMPOSE = """name: arena
services:
  traefik:
    image: traefik:v3.6.7
    container_name: traefik
  web:
    build: ../src/web
    container_name: web
  app:
    build: { context: .., dockerfile: src/Llm.Api/Dockerfile }
    container_name: app
  postgres:
    image: pgvector/pgvector:0.8.0-pg16
    container_name: postgres
  litellm:
    image: ghcr.io/berriai/litellm:main-stable
    container_name: litellm
  # A comment at two spaces.
  argus:
    build: { context: .., dockerfile: src/Argus/Dockerfile, target: server }
    container_name: argus
  cpu-temp-exporter:
    image: python:3.13-slim
    container_name: cpu-temp-exporter
volumes:
  postgres:
  engine:
"""
SERVICES = "traefik\nweb\napp\npostgres\nlitellm\nargus\ncpu-temp-exporter\n"


class Sandbox:
    def __init__(self):
        self.dir = Path(tempfile.mkdtemp(prefix="deploy-test-"))
        self.repo = self.dir / "repo"
        self.deploy = self.repo / "deploy"
        (self.deploy / "scripts").mkdir(parents=True)
        for name in SCRIPTS:
            shutil.copy2(REPO / "deploy" / "scripts" / name, self.deploy / "scripts" / name)
        (self.repo / "VERSION").write_text("9.9.9\n")
        self.write("docker-compose.yml", COMPOSE)
        self.write("podman.yml", "services: {}\n")
        self.bin = self.dir / "bin"
        self.bin.mkdir()
        for name in ("docker", "podman"):
            target = self.bin / name
            shutil.copy2(Path(__file__).with_name("fake_engine.py"), target)
            target.chmod(target.stat().st_mode | stat.S_IXUSR)
        self.log = self.dir / "engine.log"
        self.log.touch()
        self.env: dict[str, str] = {}

    def write(self, rel: str, text: str, base: Path | None = None) -> Path:
        path = (base or self.deploy) / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)
        return path

    def run(self, script: str, *args: str, cwd: Path | None = None, extra: dict[str, str] | None = None) -> subprocess.CompletedProcess:
        env = {k: v for k, v in os.environ.items() if not k.startswith(("COMPOSE_", "FAKE_"))}
        env.update({"PATH": f"{self.bin}:{env.get('PATH', '/usr/bin:/bin')}", "FAKE_LOG": str(self.log), "TMPDIR": str(self.dir)})
        env.update(self.env)
        env.update(extra or {})
        path = self.deploy / "scripts" / script
        cmd = (["python3", str(path)] if script.endswith(".py") else ["bash", str(path)]) + list(args)
        return subprocess.run(cmd, cwd=cwd or self.dir, env=env, capture_output=True, text=True, timeout=120)

    def bash(self, code: str, extra: dict[str, str] | None = None) -> subprocess.CompletedProcess:
        """Runs code with test-project.sh sourced, TP_ROOT set to the sandbox's deploy/."""
        env = {k: v for k, v in os.environ.items() if not k.startswith(("COMPOSE_", "FAKE_"))}
        env.update({"PATH": f"{self.bin}:{env.get('PATH', '/usr/bin:/bin')}", "FAKE_LOG": str(self.log), "TMPDIR": str(self.dir)})
        env.update(self.env)
        env.update(extra or {})
        prelude = f'TP_ROOT="{self.deploy}"; . "{self.deploy}/scripts/test-project.sh"\n'
        return subprocess.run(["bash", "-c", prelude + code], env=env, capture_output=True, text=True, timeout=60)

    def calls(self) -> list[list[str]]:
        return [json.loads(line) for line in self.log.read_text().splitlines() if line.strip()]

    def called(self, *words: str) -> bool:
        """Whether any engine call had these words in this order, side by side."""
        n = len(words)
        return any(c[i:i + n] == list(words) for c in self.calls() for i in range(len(c)))

    def cleanup(self) -> None:
        shutil.rmtree(self.dir, ignore_errors=True)
