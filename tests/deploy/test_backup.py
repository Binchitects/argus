"""
deploy/scripts/backup.sh: removing old backups on request (--prune, what Admin ->
Storage previews, since the app sees the backups read only), and a backups folder
Docker made root's, with fake docker on PATH (nothing runs). And the app's mount of
the folder: the host's `docker compose config`, when it has one, which only reads.
"""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import unittest

from support import REPO, Sandbox


class PruneTests(unittest.TestCase):
    def setUp(self):
        self.s = Sandbox()

    def tearDown(self):
        self.s.cleanup()

    def backups(self, where: str, *made: tuple[str, str], latest: str | None = None):
        for name, result in made:
            self.s.write(f"{where}/{name}/RESULT", result + "\n")
            self.s.write(f"{where}/{name}/postgres.sql.gz", "dump")
        if latest:
            os.symlink(latest, self.s.deploy / where / "latest")

    def left(self, where: str = "backups") -> list[str]:
        return sorted(os.listdir(self.s.deploy / where))

    def test_Beyond_the_newest_it_removes_all_but_the_latest_and_the_newest_that_ended_well(self):
        self.s.write(".env", "BACKUP_KEEP=14\n")
        self.backups("backups", ("2026-09-01_030000", "ok"), ("2026-09-02_030000", "ok"), ("2026-09-03_030000", "FAILED (1 problem(s))"),
                     ("2026-09-04_030000", "FAILED (2 problem(s))"), latest="2026-09-04_030000")
        self.s.write("backups/notes.txt", "the admin's own")
        r = self.s.run("backup.sh", "--prune", "--keep", "1")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(self.left(), [".backup.lock", "2026-09-02_030000", "2026-09-04_030000", "latest", "notes.txt"])
        self.assertIn("removed 2", r.stdout)
        self.assertIn("2026-09-02_030000 (the newest that ended well)", r.stdout)
        self.assertEqual(self.s.calls(), [])

    def test_By_default_it_keeps_BACKUP_KEEP_here_and_in_the_second_copy(self):
        self.s.write(".env", "BACKUP_KEEP=2\nBACKUP_COPY_DIR=./copies\n")
        made = [(f"2026-09-0{d}_030000", "ok") for d in range(1, 5)]
        self.backups("backups", *made, latest="2026-09-04_030000")
        self.backups("copies", *made[:3])
        r = self.s.run("backup.sh", "--prune")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(self.left(), [".backup.lock", "2026-09-03_030000", "2026-09-04_030000", "latest"])
        self.assertEqual(self.left("copies"), ["2026-09-02_030000", "2026-09-03_030000"])

    def test_A_number_that_is_not_one_removes_nothing(self):
        self.s.write(".env", "")
        self.backups("backups", ("2026-09-01_030000", "ok"), ("2026-09-02_030000", "ok"))
        for args in (["--keep", "0"], ["--keep", "two"], ["--keep"]):
            r = self.s.run("backup.sh", "--prune", *args)
            self.assertEqual(r.returncode, 1, args)
            self.assertIn("--keep", r.stderr, args)
        self.assertEqual(self.left(), ["2026-09-01_030000", "2026-09-02_030000"])

    @unittest.skipIf(os.geteuid() == 0, "root may write anywhere")
    def test_A_backups_folder_another_user_owns_is_named_with_the_fix_before_anything_runs(self):
        # A folder Docker made for the app's mount is root's: a backup run as the operator says what to do.
        self.s.write(".env", "BACKUP_DIR=/usr\n")
        r = self.s.run("backup.sh")
        self.assertEqual(r.returncode, 1)
        self.assertIn("/usr belongs to root", r.stderr)
        self.assertIn("sudo chown", r.stderr)
        self.assertIn("--install-timer", r.stderr)
        self.assertEqual(self.s.calls(), [])

    @unittest.skipIf(os.geteuid() == 0, "root may write anywhere")
    def test_An_empty_backups_folder_Docker_made_root_s_is_taken_back_through_Docker(self):
        # Docker made it for the app's mount before the first backup: nothing to do by hand. (The fake docker
        # changes nothing, so the folder stays root's here and the backup then names the fix.)
        empty = next((d for d in ("/mnt", "/srv", "/media") if os.path.isdir(d) and os.stat(d).st_uid == 0
                      and not os.access(d, os.W_OK) and not os.listdir(d)), None)
        if empty is None:
            self.skipTest("no empty folder of root's here")
        self.s.write(".env", f"BACKUP_DIR={empty}\n")
        r = self.s.run("backup.sh")
        self.assertEqual(r.returncode, 1)
        self.assertEqual(self.s.calls(), [["docker", "run", "--rm", "--network", "none", "-v", f"{empty}:/d", "python:3.13-slim",
                                           "chown", f"{os.getuid()}:{os.getgid()}", "/d"]])
        self.assertIn("sudo chown", r.stderr)


def real_compose() -> str | None:
    """The host's docker, when it has Compose: `docker compose config` only reads files."""
    docker = shutil.which("docker")
    if docker is None:
        return None
    try:
        ok = subprocess.run([docker, "compose", "version"], capture_output=True, timeout=30).returncode == 0
    except (OSError, subprocess.TimeoutExpired):
        return None
    return docker if ok else None


class ComposeMountTests(unittest.TestCase):
    """The app mounts BACKUP_DIR (Admin -> Storage): every value backup.sh takes is the same folder there."""

    def setUp(self):
        self.s = Sandbox()
        self.compose = (REPO / "deploy" / "docker-compose.yml").read_text()

    def tearDown(self):
        self.s.cleanup()

    def test_The_mount_is_written_out_as_a_bind(self):
        # In the short form Compose takes a value with no ./ or / in front (BACKUP_DIR=backups) for a named
        # volume the file does not declare, and refuses to start the stack.
        short = re.search(r"\$\{BACKUP_DIR[^}]*\}:/backups.*", self.compose)
        self.assertIsNone(short, short and short.group(0))
        self.assertIsNotNone(re.search(r"- type: bind\n\s+source: \$\{BACKUP_DIR:-\./backups\}\n\s+target: /backups\n\s+read_only: true\n", self.compose))

    def test_Compose_mounts_the_folder_backup_sh_writes_for_each_form_of_BACKUP_DIR(self):
        docker = real_compose()
        if docker is None:
            self.skipTest("no docker compose here")
        self.s.write("docker-compose.yml", self.compose)
        keys = "".join(f"{k}=x\n" for k in ("APP_KEY", "ARGUS_KEY", "DB_PASSWORD", "ENGINE_KEY", "GATEWAY_KEY"))
        elsewhere = self.s.dir / "elsewhere"
        for value in ("", "backups", "./backups", "arena-backups", "../kept", str(elsewhere)):
            with self.subTest(BACKUP_DIR=value):
                self.s.write(".env", keys + f"BACKUP_DIR={value}\n")
                env = {k: v for k, v in os.environ.items() if not k.startswith(("COMPOSE_", "BACKUP_", "DOCKER_HOST"))}
                r = subprocess.run([docker, "compose", "-f", str(self.s.deploy / "docker-compose.yml"), "config", "--format", "json"],
                                   cwd=self.s.deploy, env=env, capture_output=True, text=True, timeout=60)
                self.assertEqual(r.returncode, 0, r.stderr)
                mount = next(v for v in json.loads(r.stdout)["services"]["app"]["volumes"] if v["target"] == "/backups")
                self.assertEqual((mount["type"], mount.get("read_only")), ("bind", True))
                listed = self.s.run("backup.sh", "--list")
                self.assertEqual(listed.returncode, 0, listed.stderr)
                where = re.search(r"^Backups in (.+) \(keeping", listed.stdout, re.M).group(1)
                self.assertEqual(os.path.normpath(mount["source"]), os.path.normpath(where))


if __name__ == "__main__":
    unittest.main()
