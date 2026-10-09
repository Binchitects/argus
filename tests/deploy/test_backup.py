"""
deploy/scripts/backup.sh: removing old backups on request (--prune, what Admin ->
Storage previews, since the app sees the backups read only), and a backups folder
Docker made root's, with fake docker on PATH (nothing runs).
"""
from __future__ import annotations

import os
import unittest

from support import Sandbox


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


if __name__ == "__main__":
    unittest.main()
