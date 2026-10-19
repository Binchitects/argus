"""
installer.sh requirements: what the host needs, checked, and put right with --fix (the system's
packages, the container toolkit wired into the engine, the host's settings), each step checked
again after. Fake package managers, sudo, systemctl and nvidia-ctk on PATH log what they are asked;
nothing here changes this host.
"""
from __future__ import annotations

import os
import shutil
import subprocess
import unittest
from pathlib import Path

from support import Sandbox

UBUNTU = 'PRETTY_NAME="Ubuntu 24.04 LTS"\nID=ubuntu\nID_LIKE=debian\nVERSION_ID="24.04"\nVERSION_CODENAME=noble\n'


class RequirementsTests(unittest.TestCase):
    def setUp(self):
        self.s = Sandbox()
        self.calls = self.s.dir / "calls.log"
        self.osrel = self.s.write("os-release", UBUNTU, base=self.s.dir)
        # Only these of the host's own commands: not its nvidia-smi, nvidia-ctk or package manager.
        self.sys = self.s.dir / "sys"
        self.sys.mkdir()
        for tool in ("bash", "sh", "tar", "gzip", "sha256sum", "openssl", "ss", "flock", "awk", "sed", "grep", "cat", "head", "tail",
                     "cut", "tr", "date", "mktemp", "mkdir", "rm", "cp", "mv", "chmod", "id", "uname", "nproc", "df", "du", "dirname",
                     "basename", "sort", "comm", "find", "wc", "env", "printf", "sleep", "tee", "stat", "ls", "readlink", "touch", "od",
                     "python3", "uniq", "xargs", "true", "false"):
            found = shutil.which(tool)
            if found:
                (self.sys / tool).symlink_to(found)
        # sudo logs what it is asked; what would change the host is not run (the rest is).
        self.fake("sudo", 'echo "sudo $*" >> "$CALLS"\ncase "$1" in tee|gpg) cat >/dev/null 2>&1; exit 0 ;; mkdir|install|loginctl|usermod) exit 0 ;; esac\nexec "$@"')
        # No GPU, unless a test puts one in.
        self.fake("nvidia-smi", 'exit 9')
        self.fake("systemctl", 'echo "systemctl $*" >> "$CALLS"')
        self.fake("timedatectl", 'echo yes')
        # Offline: the vendors' repositories are out of reach, so the system's own packages are used.
        self.fake("curl", 'exit 7')
        # apt-get installs nvidia-container-toolkit by making nvidia-ctk, which logs what it is asked.
        self.fake("apt-get", 'echo "apt-get $*" >> "$CALLS"\n'
                  'case " $* " in *" nvidia-container-toolkit "*) '
                  f'printf \'#!/bin/sh\\necho "nvidia-ctk $*" >> "$CALLS"\\n\' > "{self.s.bin}/nvidia-ctk"; chmod +x "{self.s.bin}/nvidia-ctk" ;; esac')

    def tearDown(self):
        self.s.cleanup()

    def fake(self, name: str, body: str) -> None:
        path = self.s.bin / name
        path.write_text("#!/bin/sh\n" + body + "\n")
        path.chmod(0o755)

    def run_req(self, *args: str, extra: dict[str, str] | None = None) -> subprocess.CompletedProcess:
        env = {"CALLS": str(self.calls), "ARENA_OS_RELEASE": str(self.osrel), "ARENA_GPU_HARDWARE": "yes",
               "PATH": f"{self.s.bin}:{self.sys}"}
        env.update(extra or {})
        return self.s.run("installer.sh", "requirements", "--docker", "--http-port", "18080", "--https-port", "18443", *args, extra=env)

    def logged(self) -> str:
        return self.calls.read_text() if self.calls.exists() else ""

    def test_A_host_that_has_everything_is_told_so(self):
        r = self.run_req("--cpu-only")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("system: Ubuntu 24.04 LTS", r.stdout)
        self.assertIn("packages: apt", r.stdout)
        self.assertIn("GPU: none asked for (--cpu-only)", r.stdout)
        self.assertIn("Every requirement is met.", r.stdout)

    def test_A_card_without_its_driver_is_named_with_the_fix_and_the_way_round_it(self):
        r = self.run_req()
        self.assertEqual(r.returncode, 3, r.stdout + r.stderr)
        self.assertIn("an NVIDIA card is there but its driver is not", r.stdout)
        self.assertIn("--cpu-only", r.stdout)
        self.assertIn("install the NVIDIA driver (a reboot follows", r.stdout)
        self.assertIn("installer.sh requirements --fix", r.stdout)

    def test_No_card_at_all_points_to_cpu_only(self):
        r = self.run_req(extra={"ARENA_GPU_HARDWARE": "no"})
        self.assertEqual(r.returncode, 3)
        self.assertIn("no NVIDIA GPU here: --cpu-only", r.stdout)

    def test_A_dry_run_of_fix_says_what_it_would_do_and_does_nothing(self):
        self.s.bin.joinpath("nvidia-smi").write_text('#!/bin/sh\necho "GPU 0: Fake GPU (UUID: GPU-0)"\n')
        self.s.bin.joinpath("nvidia-smi").chmod(0o755)
        r = self.run_req("--fix", "--dry-run")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("NVIDIA's container toolkit is not installed", r.stdout)
        self.assertIn("would  install NVIDIA's container toolkit and wire it into docker", r.stdout)
        self.assertEqual(self.logged(), "")

    def test_Fix_installs_the_toolkit_wires_it_into_docker_and_checks_again(self):
        self.s.bin.joinpath("nvidia-smi").write_text('#!/bin/sh\necho "GPU 0: Fake GPU (UUID: GPU-0)"\n')
        self.s.bin.joinpath("nvidia-smi").chmod(0o755)
        r = self.run_req("--fix", "--yes")
        out = r.stdout + r.stderr
        calls = self.logged()
        # The system's own package (its repository out of reach), then the runtime and a restart of Docker.
        self.assertIn("nvidia.github.io is out of reach", out)
        self.assertIn("apt-get install -y -qq nvidia-container-toolkit", calls)
        self.assertIn("nvidia-ctk runtime configure --runtime=docker", calls)
        self.assertIn("systemctl restart docker", calls)
        self.assertIn("done   install NVIDIA's container toolkit", out)
        # Checked again after: a second requirements pass.
        self.assertEqual(out.count("==> requirements"), 2, out)

    def test_On_Podman_fix_writes_a_CDI_spec_and_keeps_it_current(self):
        self.s.bin.joinpath("nvidia-smi").write_text('#!/bin/sh\necho "GPU 0: Fake GPU (UUID: GPU-0)"\n')
        self.s.bin.joinpath("nvidia-smi").chmod(0o755)
        env = {"CALLS": str(self.calls), "ARENA_OS_RELEASE": str(self.osrel), "ARENA_GPU_HARDWARE": "yes", "PATH": f"{self.s.bin}:{self.sys}",
               "ARENA_CDI_DIRS": str(self.s.dir / "cdi")}
        r = self.s.run("installer.sh", "requirements", "--podman", "--http-port", "18080", "--https-port", "18443", "--fix", "--yes", extra=env)
        calls = self.logged()
        self.assertIn("nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml", calls, r.stdout + r.stderr)
        self.assertIn("systemctl enable --now nvidia-cdi-refresh.path", calls)

    def test_Install_does_not_change_the_host_unattended_unless_asked(self):
        r = self.s.run("installer.sh", "install", "--yes", "--skip-requirements", "--dry-run", "--install-requirements")
        # --install-requirements is install's; requirements' own is --fix.
        self.assertNotEqual(r.returncode, 2, r.stderr)
        r = self.s.run("installer.sh", "requirements", "--install-requirements")
        self.assertEqual(r.returncode, 2)
        self.assertIn("--install-requirements is for install", r.stderr)


if __name__ == "__main__":
    unittest.main()
