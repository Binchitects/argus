"""
deploy/scripts/gpu-check.sh, with fake nvidia-smi and rocm-smi on PATH (nothing real is asked): a good four-GPU server,
an old driver, GPUs without peer to peer or across sockets, a low power cap, MIG on, an AMD host, a CPU-only host, and
NCCL's all-reduce read.
"""
from __future__ import annotations

import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from support import REPO

CHECK = REPO / "deploy" / "scripts" / "gpu-check.sh"

GOOD_P2P = """        GPU0    GPU1    GPU2    GPU3
 GPU0   X       OK      OK      OK
 GPU1   OK      X       OK      OK
 GPU2   OK      OK      X       OK
 GPU3   OK      OK      OK      X
"""
GOOD_TOPO = """        GPU0    GPU1    GPU2    GPU3    CPU Affinity
GPU0     X      PIX     PIX     PIX     0-63
GPU1    PIX      X      PIX     PIX     0-63
GPU2    PIX     PIX      X      PIX     0-63
GPU3    PIX     PIX     PIX      X      0-63
"""


class GpuCheckTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="gpu-check-"))
        self.bin = self.dir / "bin"
        self.bin.mkdir()
        for tool in ("sh", "grep", "sed", "awk", "head", "tr", "df", "cat"):
            (self.bin / tool).symlink_to(shutil.which(tool))
        self.addCleanup(shutil.rmtree, self.dir, True)

    def smi(self, driver="595.99.02", p2p=GOOD_P2P, topo=GOOD_TOPO, power="600.00, 600.00", mig="Disabled", count=4):
        lines = "\n".join(f"GPU {i}: NVIDIA RTX PRO 6000 Blackwell Server Edition (UUID: GPU-{i})" for i in range(count))
        rows = "\n".join(f"{i}, NVIDIA RTX PRO 6000 Blackwell Server Edition, 97887 MiB, Enabled, {mig}" for i in range(count))
        migs = "\n".join(f"{i}, {mig}" for i in range(count))
        powers = "\n".join(f"{i}, {power}" for i in range(count))
        script = f"""#!/bin/sh
case "$*" in
  "-L") cat <<'EOF'
{lines}
EOF
;;
  *driver_version*) echo "{driver}" ;;
  *ecc.mode.current*) cat <<'EOF'
{rows}
EOF
;;
  *mig.mode.current*) cat <<'EOF'
{migs}
EOF
;;
  *power.limit*) cat <<'EOF'
{powers}
EOF
;;
  "topo -p2p r") cat <<'EOF'
{p2p}EOF
;;
  "topo -m") cat <<'EOF'
{topo}EOF
;;
esac
"""
        p = self.bin / "nvidia-smi"
        p.write_text(script)
        p.chmod(0o755)

    def run_check(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run(["/bin/sh", str(CHECK), *args], env={"PATH": str(self.bin)}, capture_output=True, text=True, timeout=60)

    def test_a_good_four_gpu_server_passes(self):
        self.smi()
        r = self.run_check()
        self.assertEqual(0, r.returncode, r.stdout)
        self.assertIn("OK    driver 595.99.02", r.stdout)
        self.assertIn("OK    4 GPU(s):", r.stdout)
        self.assertIn("peer-to-peer reads between every pair", r.stdout)
        self.assertIn("no SYS link", r.stdout)
        self.assertNotIn("FAIL", r.stdout)

    def test_an_old_driver_missing_p2p_and_a_low_cap_are_said(self):
        self.smi(driver="560.35.03", p2p=GOOD_P2P.replace("OK      X       OK      OK", "NS      X       OK      OK"),
                 topo=GOOD_TOPO.replace("GPU3    PIX     PIX     PIX", "GPU3    SYS     SYS     SYS"), power="200.00, 600.00", mig="Enabled")
        r = self.run_check()
        self.assertEqual(1, r.returncode)
        self.assertIn("FAIL  driver 560.35.03", r.stdout)
        self.assertIn("FAIL  peer-to-peer is not supported", r.stdout)
        self.assertIn("WARN  some GPUs connect through the CPU sockets", r.stdout)
        self.assertIn("is capped at", r.stdout)
        self.assertIn("MIG is on", r.stdout)

    def test_nccl_all_reduce_is_read(self):
        self.smi()
        nccl = self.bin / "all_reduce_perf"
        nccl.write_text("#!/bin/sh\necho '# Out of bounds values : 0 OK'\necho '# Avg bus bandwidth    : 41.27 '\n")
        nccl.chmod(0o755)
        r = self.run_check("--nccl", str(nccl))
        self.assertEqual(0, r.returncode, r.stdout)
        self.assertIn("NCCL all-reduce: 41.27 GB/s", r.stdout)
        self.assertEqual(1, self.run_check("--nccl", str(self.dir / "missing")).returncode)

    def test_an_amd_host_and_a_cpu_only_host(self):
        rocm = self.bin / "rocm-smi"
        rocm.write_text("#!/bin/sh\ncase \"$*\" in *showtopotype*) echo 'GPU0 GPU1 XGMI';; *) echo 'Card series: AMD Instinct MI300X';; esac\n")
        rocm.chmod(0o755)
        r = self.run_check()
        self.assertEqual(0, r.returncode, r.stdout)
        self.assertIn("AMD GPUs", r.stdout)
        self.assertIn("XGMI", r.stdout)
        rocm.unlink()
        r = self.run_check()
        self.assertEqual(0, r.returncode, r.stdout)
        self.assertIn("no GPU tool", r.stdout)


if __name__ == "__main__":
    unittest.main()
