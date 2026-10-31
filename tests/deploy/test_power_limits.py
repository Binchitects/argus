"""
deploy/services/power-limits.sh: the power caps and the temperature policy, one check at a time
(POWER_ONCE), against a fake nvidia-smi and a fake /sys (POWER_SYS) with Intel's RAPL limits or a
CPU's cpufreq. Nothing here touches the machine's own GPU or CPU.
"""
from __future__ import annotations

import os
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "deploy" / "services" / "power-limits.sh"

# A fake nvidia-smi: the GPUs in gpus.csv (index, temperature, limit, default, lowest), each -pl written back.
FAKE_SMI = textwrap.dedent("""\
    #!/usr/bin/env python3
    import sys, os
    path = os.environ["FAKE_GPUS"]
    rows = [l.split(",") for l in open(path).read().split()]
    args = sys.argv[1:]
    if "-pl" in args:
        i, w = args[args.index("-i") + 1], args[args.index("-pl") + 1]
        if os.environ.get("FAKE_REFUSE"): sys.exit(1)
        for r in rows:
            if r[0] == i: r[2] = w
        open(path, "w").write("\\n".join(",".join(r) for r in rows) + "\\n")
        open(path + ".calls", "a").write(f"{i} {w}\\n")
    else:
        for r in rows: print(", ".join(r))
""")


class PowerLimitsTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="power-"))
        (self.dir / "bin").mkdir()
        (self.dir / "state").mkdir()
        smi = self.dir / "bin" / "nvidia-smi"
        smi.write_text(FAKE_SMI)
        smi.chmod(0o755)
        self.gpus = self.dir / "gpus.csv"
        self.sys = self.dir / "sys"

    def tearDown(self):
        subprocess.run(["rm", "-rf", str(self.dir)], check=False)

    def gpu(self, temp: int, limit: int = 350, default: int = 350, lowest: int = 100):
        self.gpus.write_text(f"0,{temp},{limit}.00,{default}.00,{lowest}.00\n")

    def limit(self) -> int:
        return int(float(self.gpus.read_text().split(",")[2]))

    def rapl(self, temp: int, uw: int = 125_000_000):
        h = self.sys / "class" / "hwmon" / "hwmon0"
        h.mkdir(parents=True, exist_ok=True)
        (h / "name").write_text("coretemp\n")
        (h / "temp1_input").write_text(f"{temp * 1000}\n")
        (h / "temp2_input").write_text(f"{(temp - 8) * 1000}\n")
        d = self.sys / "class" / "powercap" / "intel-rapl:0"
        d.mkdir(parents=True, exist_ok=True)
        for c in (0, 1):
            f = d / f"constraint_{c}_power_limit_uw"
            if not f.exists():
                f.write_text(f"{uw}\n")
        return d

    def cpufreq(self, temp: int, cores: int = 2):
        h = self.sys / "class" / "hwmon" / "hwmon0"
        h.mkdir(parents=True, exist_ok=True)
        (h / "name").write_text("k10temp\n")
        (h / "temp1_input").write_text(f"{temp * 1000}\n")
        for n in range(cores):
            c = self.sys / "devices" / "system" / "cpu" / f"cpu{n}" / "cpufreq"
            c.mkdir(parents=True, exist_ok=True)
            (c / "cpuinfo_max_freq").write_text("5000000\n")
            (c / "cpuinfo_min_freq").write_text("800000\n")
            if not (c / "scaling_max_freq").exists():
                (c / "scaling_max_freq").write_text("5000000\n")

    def check(self, **env) -> str:
        e = {"PATH": f"{self.dir / 'bin'}:/usr/bin:/bin", "FAKE_GPUS": str(self.gpus), "POWER_SYS": str(self.sys),
             "POWER_STATE": str(self.dir / "state"), "POWER_ONCE": "1"} | {k: str(v) for k, v in env.items()}
        if not self.gpus.exists():
            e["PATH"] = "/usr/bin:/bin"
        out = subprocess.run(["/bin/sh", str(SCRIPT)], env=e, capture_output=True, text=True, timeout=30)
        self.assertEqual(0, out.returncode, out.stderr)
        return out.stdout

    def test_a_hot_gpu_steps_down_to_its_floor_a_critical_one_to_its_lowest_and_a_cool_one_comes_back(self):
        self.gpu(85)
        out = self.check()
        self.assertEqual(315, self.limit())
        self.assertIn("gpu0: 85 C (limit 83 C, critical 90 C): power limit 350 W -> 315 W", out)
        for _ in range(8):
            self.check()
        self.assertEqual(175, self.limit(), "never below half the ceiling while not critical")
        self.gpu(92, limit=self.limit())
        self.check()
        self.assertEqual(100, self.limit())
        # Cool again: a step up each check, back to the ceiling and no further.
        self.gpu(70, limit=self.limit())
        for _ in range(12):
            self.check()
        self.assertEqual(350, self.limit())

    def test_between_cool_and_hot_the_cap_holds(self):
        self.gpu(85)
        self.check()
        self.gpu(80, limit=self.limit())
        self.check()
        self.assertEqual(315, self.limit())

    def test_the_cap_from_env_is_the_ceiling_and_off_keeps_only_it(self):
        self.gpu(60)
        self.check(GPU_POWER_LIMIT_W=150)
        self.assertEqual(150, self.limit())
        self.gpu(86, limit=150)
        self.check(GPU_POWER_LIMIT_W=150)
        self.assertEqual(115, self.limit(), "a step is a tenth of the card's own")
        self.gpu(95, limit=115)
        self.check(GPU_POWER_LIMIT_W=150, THERMAL_POLICY="off")
        self.assertEqual(150, self.limit(), "off: the cap, whatever the temperature")

    def test_a_card_that_refuses_a_limit_is_said_once(self):
        self.gpu(86)
        first = self.check(FAKE_REFUSE=1)
        second = self.check(FAKE_REFUSE=1)
        self.assertIn("refused a power limit", first)
        self.assertNotIn("refused", second)

    def test_intel_rapl_steps_down_when_hot_and_a_rounded_read_back_is_not_written_again(self):
        d = self.rapl(93)
        out = self.check()
        self.assertEqual(112_500_000, int((d / "constraint_0_power_limit_uw").read_text()))
        self.assertIn("cpu intel-rapl:0: 93 C (limit 90 C, critical 95 C): limit 125 W -> 112 W", out)
        # The kernel reads back a little off what was written: not written again, nothing said, once cool.
        (d / "constraint_0_power_limit_uw").write_text("124999872\n")
        (d / "constraint_1_power_limit_uw").write_text("124999872\n")
        for f in (self.dir / "state").glob("intel-rapl:0.c*.target"):
            f.write_text("125000000\n")
        self.rapl(60)
        self.assertEqual([], self.check().splitlines()[1:], "only its first line, what it keeps to")

    def test_the_cpu_cap_from_env_is_kept_and_the_firmwares_comes_back_without_it(self):
        d = self.rapl(50)
        self.check(CPU_POWER_LIMIT_W=95)
        self.assertEqual(95_000_000, int((d / "constraint_0_power_limit_uw").read_text()))
        # Without it, the firmware's again, a step at a time as after a hot spell; a lower cap is kept at once.
        seen = []
        for _ in range(4):
            self.check()
            seen.append(int((d / "constraint_0_power_limit_uw").read_text()) // 1_000_000)
        self.assertEqual([107, 120, 125, 125], seen)
        self.check(CPU_POWER_LIMIT_W=80)
        self.assertEqual(80_000_000, int((d / "constraint_0_power_limit_uw").read_text()))

    def test_any_other_cpu_steps_its_highest_frequency_down_on_every_core(self):
        self.cpufreq(96)
        out = self.check()
        for n in (0, 1):
            f = self.sys / "devices" / "system" / "cpu" / f"cpu{n}" / "cpufreq" / "scaling_max_freq"
            self.assertEqual(2_500_000, int(f.read_text()), "critical: straight to the floor")
        self.assertIn("highest frequency 5000 MHz -> 2500 MHz", out)
        self.cpufreq(80)
        for _ in range(6):
            self.check()
        f = self.sys / "devices" / "system" / "cpu" / "cpu1" / "cpufreq" / "scaling_max_freq"
        self.assertEqual(5_000_000, int(f.read_text()))


if __name__ == "__main__":
    unittest.main()
