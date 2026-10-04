"""
deploy/scripts/restore-test.sh, rollback-test.sh and test-project.sh: their
arguments, their plans, and the guards that keep them off the live project,
with fake docker on PATH (nothing runs).
"""
from __future__ import annotations

import gzip
import socket
import subprocess
import unittest

from support import SERVICES, Sandbox


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def backup(s: Sandbox, stamp: str, env: str = "DOMAIN=llm.example\nADMIN_PASSWORD=pw-from-backup\nHTTPS_PORT=443\nMODELS_DIR=/srv/models\nCOMPOSE_PROFILES=off\n"):
    d = s.deploy / "backups" / stamp
    s.write("config/.env", env, base=d)
    with gzip.open(d / "postgres.sql.gz", "wt") as f:
        f.write("-- PostgreSQL database cluster dump complete\n")
    return d


class GuardTests(unittest.TestCase):
    """test-project.sh: what may be touched, and what never."""

    def setUp(self):
        self.s = Sandbox()
        self.s.write(".env", "COMPOSE_PROJECT_NAME=prod-test-stack\nHTTPS_PORT=8443\n")

    def tearDown(self):
        self.s.cleanup()

    def test_The_live_project_and_names_that_do_not_say_test_are_refused(self):
        for name, message in [
            ("arena", "is the live project"),
            ("prod-test-stack", "is the live project"),
            ("arena-copy", "says test"),
            ("arena_test", "lowercase letters, digits and - only"),
            ("Arena-Test", "lowercase letters, digits and - only"),
            ("", "lowercase letters"),
        ]:
            r = self.s.bash(f'tp_guard_project "{name}"')
            self.assertEqual(r.returncode, 1, name)
            self.assertIn(message, r.stderr, name)
        self.assertEqual(self.s.bash("tp_guard_project arena-restore-test").returncode, 0)

    def test_The_live_ports_and_ports_in_use_are_refused(self):
        for port, message in [("443", "live stack's"), ("80", "live stack's"), ("8443", "live stack's"), ("1000", "from 1024"), ("x", "from 1024")]:
            r = self.s.bash(f"tp_guard_port {port}")
            self.assertEqual(r.returncode, 1, port)
            self.assertIn(message, r.stderr, port)
        with socket.socket() as busy:
            busy.bind(("127.0.0.1", 0))
            busy.listen()
            r = self.s.bash(f"tp_guard_port {busy.getsockname()[1]}")
            self.assertEqual(r.returncode, 1)
            self.assertIn("in use", r.stderr)
        self.assertEqual(self.s.bash(f"tp_guard_port {free_port()}").returncode, 0)

    def test_Only_the_throwaway_projects_volumes_and_images_are_ever_removed(self):
        r = self.s.bash("TP_PROJECT=arena-restore-test; tp_volume_rm arena_postgres")
        self.assertEqual(r.returncode, 1)
        self.assertIn("not arena-restore-test's", r.stderr)
        r = self.s.bash("TP_PROJECT=arena-restore-test; tp_image_rm arena-app")
        self.assertEqual(r.returncode, 1)
        r = self.s.bash("TP_PROJECT=arena; tp_volume_rm arena_postgres")
        self.assertEqual(r.returncode, 1)
        self.assertIn("is the live project", r.stderr)
        self.assertFalse(self.s.called("volume", "rm") or self.s.called("image", "rm"))

        volumes = "arena_postgres\narena_audio\narena-restore-test_postgres\narena-restore-test_argus\narena-restore-test-other_x\nmy-arena-restore-test_y\n"
        r = self.s.bash("TP_PROJECT=arena-restore-test; tp_down", {"FAKE_VOLUME_LIST": volumes})
        self.assertEqual(r.returncode, 0, r.stderr)
        removed = sorted(c[-1] for c in self.s.calls() if c[1:3] == ["volume", "rm"])
        self.assertEqual(removed, ["arena-restore-test_argus", "arena-restore-test_postgres"])
        self.assertTrue(self.s.called("ps", "-aq", "--filter", "label=com.docker.compose.project=arena-restore-test"))

    def test_Compose_is_aimed_at_the_throwaway_project_only(self):
        r = self.s.bash(
            f'tp_init arena-restore-test 18443 "{self.s.dir}/w"; TP_COMPOSE=/c.yml; tp_run env',
            {"COMPOSE_PROFILES": "off", "COMPOSE_PROJECT_NAME": "arena", "MODELS_DIR": "/live/models", "HTTPS_PORT": "443"})
        self.assertEqual(r.returncode, 0, r.stderr)
        env = dict(line.split("=", 1) for line in r.stdout.splitlines() if "=" in line)
        self.assertEqual(env["COMPOSE_PROJECT_NAME"], "arena-restore-test")
        self.assertNotIn("COMPOSE_PROFILES", env)
        self.assertEqual(env["HTTPS_PORT"], "18443")
        self.assertEqual(env["MODELS_DIR"], f"{self.s.dir}/w/models")
        self.assertEqual(env["COMPOSE_FILE"], f"/c.yml:{self.s.dir}/w/images.yml:{self.s.dir}/w/isolate.yml")
        self.assertEqual(env["COMPOSE_ENV_FILES"], f"{self.s.dir}/w/test.env")

    def test_The_env_file_keeps_its_lines_but_the_ones_set_or_dropped(self):
        w = self.s.dir / "w"
        r = self.s.bash(f'tp_init arena-restore-test 18443 "{w}"; printf "APP_KEY=k\\nHTTPS_PORT=443\\nCOMPOSE_PROFILES=off\\n" > "$TP_ENV"; '
                        'tp_env_set HTTPS_PORT 18443; tp_env_set COMPOSE_PROFILES; tp_env_set MODEL ""; stat -c %a "$TP_ENV"')
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(r.stdout.strip(), "600")
        self.assertEqual((w / "test.env").read_text(), "APP_KEY=k\nHTTPS_PORT=18443\nMODEL=\n")

    def test_The_override_leaves_out_all_but_the_core_and_fixes_no_name(self):
        r = self.s.bash(f'tp_init arena-restore-test 18443 "{self.s.dir}/w"; tp_use "{self.s.deploy}/docker-compose.yml" arena-app arena-web; cat "$TP_ISOLATE"; cat "$TP_IMAGES"',
                        {"FAKE_SERVICES": SERVICES})
        self.assertEqual(r.returncode, 0, r.stderr)
        out = r.stdout
        self.assertEqual(out.count("container_name: !reset null"), len(SERVICES.split()))
        self.assertIn("default: { internal: true }", out)
        self.assertIn('ports: !override ["127.0.0.1:18443:443"]', out)
        self.assertEqual(out.count("profiles: [off]"), 2)  # argus, cpu-temp-exporter
        self.assertEqual(out.count("build: !reset null"), 2)  # app, web: never built
        self.assertEqual(out.count("pull_policy: never"), 5)
        self.assertIn('app: { image: "arena-app" }', out)
        # Without compose (no services listed), the file's own services are read.
        r = self.s.bash(f'tp_init arena-restore-test 18443 "{self.s.dir}/w2"; tp_use "{self.s.deploy}/docker-compose.yml" a w; cat "$TP_ISOLATE"')
        self.assertEqual(r.stdout.count("container_name: !reset null"), len(SERVICES.split()))


class RestoreTestScript(unittest.TestCase):
    def setUp(self):
        self.s = Sandbox()
        self.s.write(".env", "BACKUP_DIR=./backups\n")
        self.s.env = {"FAKE_SERVICES": SERVICES}
        self.port = str(free_port())

    def tearDown(self):
        self.s.cleanup()

    def test_The_live_project_is_refused_before_anything_runs(self):
        backup(self.s, "2026-01-01_000000")
        for project in ("arena", "arena-staging"):
            r = self.s.run("restore-test.sh", "--project", project, "--port", self.port)
            self.assertEqual(r.returncode, 1, project)
        self.assertEqual(self.s.calls(), [])
        r = self.s.run("restore-test.sh", "--project", "arena", "--down")
        self.assertEqual(r.returncode, 1)
        self.assertEqual(self.s.calls(), [])

    def test_A_dry_run_restores_the_newest_backup_on_paper_only(self):
        backup(self.s, "2026-01-01_000000")
        newest = backup(self.s, "2026-02-01_000000")
        (self.s.deploy / "backups" / "2026-03-01_000000.part").mkdir()
        r = self.s.run("restore-test.sh", "--dry-run", "--port", self.port)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn(f"Would restore {newest} into arena-restore-test", r.stdout)
        self.assertIn("live project left alone: arena", r.stdout)
        self.assertIn("backup.sh --restore --from", r.stdout)
        self.assertIn(f"at https://llm.example:{self.port} (127.0.0.1)", r.stdout)
        self.assertIn("arena-app arena-web traefik:v3.6.7 pgvector/pgvector:0.8.0-pg16 ghcr.io/berriai/litellm:main-stable python:3.13-slim", r.stdout)
        self.assertIn("internal: true", r.stdout)
        # Nothing but reading the compose file's services.
        self.assertTrue(all(c[1:2] == ["compose"] and "config" in c for c in self.s.calls()), self.s.calls())
        self.assertNotIn("pw-from-backup", r.stdout + r.stderr)

    def test_The_app_and_web_images_come_from_the_project_named(self):
        backup(self.s, "2026-01-01_000000")
        r = self.s.run("restore-test.sh", "--dry-run", "--port", self.port, "--images-from", "staging")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("staging-app staging-web", r.stdout)

    def test_A_backup_it_cannot_use_is_named(self):
        r = self.s.run("restore-test.sh", "--dry-run", "--port", self.port)
        self.assertEqual(r.returncode, 1)
        self.assertIn("no backup in", r.stderr)
        d = backup(self.s, "2026-01-01_000000")
        (d / "config" / ".env").unlink()
        r = self.s.run("restore-test.sh", "--dry-run", "--port", self.port, "--from", str(d))
        self.assertEqual(r.returncode, 1)
        self.assertIn("has no config/.env", r.stderr)
        r = self.s.run("restore-test.sh", "--dry-run", "--port", self.port, "--from", str(self.s.dir / "nowhere"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("no backup at", r.stderr)

    def test_An_image_that_is_not_here_stops_it_before_anything_starts(self):
        backup(self.s, "2026-01-01_000000")
        r = self.s.run("restore-test.sh", "--port", self.port, extra={"FAKE_MISSING": "arena-app"})
        self.assertEqual(r.returncode, 1)
        self.assertIn("arena-app is not on this host", r.stderr)
        self.assertFalse(self.s.called("up") or self.s.called("run") or self.s.called("rm"))

    def test_Arguments_are_checked(self):
        for args, message in [(["--from"], "needs a backup directory"), (["--fast"], "unknown option"), (["--port"], "needs a number")]:
            r = self.s.run("restore-test.sh", *args)
            self.assertEqual(r.returncode, 2, args)
            self.assertIn(message, r.stderr)
        r = self.s.run("restore-test.sh", "--help")
        self.assertEqual(r.returncode, 0)
        self.assertIn("restore-test.sh --from DIR", r.stdout)


class RollbackTestScript(unittest.TestCase):
    """The sandbox is a git repository with two releases, v1.0.0 and v2.0.0."""

    def setUp(self):
        self.s = Sandbox()
        self.s.env = {"FAKE_SERVICES": SERVICES}
        self.port = str(free_port())
        git = ["git", "-c", "user.name=t", "-c", "user.email=t@example.test", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false"]
        repo = str(self.s.repo)
        subprocess.run(["git", "init", "-q", repo], check=True)
        for version in ("1.0.0", "2.0.0"):
            (self.s.repo / "VERSION").write_text(version + "\n")
            subprocess.run(git + ["-C", repo, "add", "-A"], check=True)
            subprocess.run(git + ["-C", repo, "commit", "-qm", version], check=True)
            subprocess.run(git + ["-C", repo, "tag", f"v{version}"], check=True)

    def tearDown(self):
        self.s.cleanup()

    def test_A_dry_run_names_the_releases_the_images_and_the_steps(self):
        r = self.s.run("rollback-test.sh", "v2.0.0", "v1.0.0", "--dry-run", "--port", self.port)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Would roll v2.0.0 (2.0.0) back to v1.0.0 (1.0.0) in arena-rollback-test", r.stdout)
        self.assertIn("arena-rollback-test-app:v1.0.0 arena-rollback-test-web:v1.0.0 arena-rollback-test-app:v2.0.0", r.stdout)
        self.assertIn("backup.sh --restore --from <the backup from 1> --yes", r.stdout)
        self.assertIn("internal: true", r.stdout)
        self.assertTrue(all(c[1:2] == ["compose"] and "config" in c for c in self.s.calls()), self.s.calls())
        worktrees = subprocess.run(["git", "-C", str(self.s.repo), "worktree", "list"], capture_output=True, text=True).stdout
        self.assertEqual(len(worktrees.splitlines()), 1)

    def test_Two_different_releases_that_exist_are_needed(self):
        for args, code, message in [
            (["v2.0.0"], 2, "name two releases"),
            (["v2.0.0", "v2.0.0"], 2, "are the same"),
            (["v2.0.0", "v0.9.0"], 1, "no release v0.9.0"),
            (["v2.0.0", "v1.0.0", "--project", "arena"], 1, "is the live project"),
            (["v2.0.0", "v1.0.0", "--project", "rollback"], 1, "says test"),
            (["v2.0.0", "v1.0.0", "--port", "443"], 1, "live stack's"),
        ]:
            r = self.s.run("rollback-test.sh", "--dry-run", *args)
            self.assertEqual(r.returncode, code, args)
            self.assertIn(message, r.stderr, args)
        self.assertEqual(self.s.calls(), [])


if __name__ == "__main__":
    unittest.main()
