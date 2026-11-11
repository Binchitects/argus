"""deploy/config/dcgm-counters.csv: dcgm-exporter collects every DCGM field a dashboard panel or an alert rule reads.

The image's own default-counters.csv leaves out SM activity, NVLink bytes, throttle reasons, ECC and retired pages, so
those panels were empty on every GPU until compose gave the exporter this file.
"""
from __future__ import annotations

import re
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
COUNTERS = REPO / "deploy/config/dcgm-counters.csv"
READERS = [*sorted((REPO / "src/Llm.Api/Dashboards/json").glob("*.json")),
           *sorted((REPO / "deploy/config/prometheus/rules").glob("*.yml")),
           *sorted((REPO / "deploy/helm/arena/files/prometheus/rules").glob("*.yml"))]


def collected() -> list[list[str]]:
    return [[f.strip() for f in line.split(",")] for line in COUNTERS.read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.startswith("#")]


class DcgmCountersTests(unittest.TestCase):
    def test_every_field_a_panel_or_rule_reads_is_collected(self):
        fields = {row[0] for row in collected()}
        for path in READERS:
            for name in sorted(set(re.findall(r"DCGM_FI_[A-Z0-9_]+", path.read_text(encoding="utf-8")))):
                with self.subTest(file=path.relative_to(REPO).as_posix(), field=name):
                    self.assertIn(name, fields)

    def test_each_line_has_the_three_fields_the_exporter_reads(self):
        for row in collected():
            with self.subTest(field=row[0]):
                self.assertEqual(3, len(row), "field, type, help (a comma in the help text breaks the file)")
                self.assertIn(row[1], ("gauge", "counter", "label"))

    def test_compose_gives_the_exporter_the_file(self):
        compose = (REPO / "deploy/docker-compose.yml").read_text(encoding="utf-8")
        block = compose[compose.index("  dcgm-exporter:"):]
        block = block[:block.index("\n\n")]
        self.assertIn('command: ["-f", "/etc/dcgm-exporter/arena-counters.csv"]', block)
        self.assertIn("./config/dcgm-counters.csv:/etc/dcgm-exporter/arena-counters.csv:ro", block)


if __name__ == "__main__":
    unittest.main()
