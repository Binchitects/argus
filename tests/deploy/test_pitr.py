"""
Point-in-time recovery (deploy/pitr.yml): backup.sh --pitr runs pgBackRest in the stack's Postgres, and restore-drill.sh
restores the repository into a scratch container and volume of its own, never the stack's, with fake docker on PATH
(nothing runs). And pitr.yml as compose reads it: WAL archived continuously, the repository on the backup disk.
"""
from __future__ import annotations

import re
import shutil
import subprocess
import tempfile
import unittest

from support import REPO, Sandbox


class PitrScriptTests(unittest.TestCase):
    def setUp(self):
        self.s = Sandbox()

    def tearDown(self):
        self.s.cleanup()

    def test_backup_pitr_runs_a_full_or_differential_backup_in_the_postgres_container(self):
        self.s.write(".env", "")
        r = self.s.run("backup.sh", "--pitr", "full")
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertTrue(self.s.called("exec", "-T", "-u", "postgres", "postgres", "pgbackrest", "--stanza=arena", "--type=full", "backup"), self.s.calls())
        r = self.s.run("backup.sh", "--pitr", "weekly")
        self.assertNotEqual(0, r.returncode)
        self.assertIn("full or diff", r.stderr)

    def test_the_drill_restores_into_a_scratch_container_never_the_stacks(self):
        self.s.write(".env", "BACKUP_DIR=./backups\n")
        r = self.s.run("restore-drill.sh", "--dry-run")
        self.assertNotEqual(0, r.returncode)
        self.assertIn("no pgBackRest repository", r.stderr)
        self.s.write("backups/pgbackrest/backup.info", "x")
        r = self.s.run("restore-drill.sh", "--dry-run", "--to", "2026-10-16 09:30:00+00")
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertIn("--type=time --target=2026-10-16 09:30:00+00", r.stdout)
        self.assertIn("arena-restore-drill-", r.stdout)
        # Never the stack's volume, container or port.
        self.assertNotRegex(r.stdout, r"arena_postgres|\b-p\b|--publish|postgres:/var/lib")
        self.assertIn("/backup:ro", r.stdout)


class PitrComposeTests(unittest.TestCase):
    def test_pitr_yml_archives_wal_to_the_backup_disk(self):
        docker = shutil.which("docker")
        if docker is None:
            self.skipTest("no docker here")
        deploy = REPO / "deploy"
        env = (deploy / ".env.example").read_text()
        env = re.sub(r"(?m)^(ADMIN_PASSWORD|DB_PASSWORD|APP_KEY|ENGINE_KEY|ARGUS_KEY|GATEWAY_KEY)=$", r"\1=x", env)
        env = re.sub(r"(?m)^DATA_DIR=$", "DATA_DIR=/srv/arena/data", env)

        with tempfile.NamedTemporaryFile("w", suffix=".env") as f:
            f.write(env)
            f.flush()
            out = subprocess.run([docker, "compose", "--env-file", f.name, "-f", str(deploy / "docker-compose.yml"), "-f", str(deploy / "pitr.yml"), "config", "postgres"],
                                 capture_output=True, text=True, timeout=120)
        self.assertEqual(0, out.returncode, out.stderr[-1500:])
        self.assertIn("archive_command=pgbackrest --stanza=arena archive-push %p", out.stdout)
        self.assertIn("target: /backup", out.stdout)
        self.assertIn("dockerfile: postgres.Dockerfile", out.stdout)
        conf = (deploy / "config" / "pgbackrest" / "pgbackrest.conf").read_text()
        self.assertIn("repo1-path=/backup/pgbackrest", conf)
        self.assertIn("[arena]", conf)
        self.assertRegex((deploy / "services" / "postgres.Dockerfile").read_text(), r"ARG POSTGRES_IMAGE=pgvector/pgvector:0\.8\.0-pg16")


if __name__ == "__main__":
    unittest.main()
