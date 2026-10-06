#!/usr/bin/env python3
"""CPU temperature for Prometheus, on Linux and on Windows.

Neither of the stack's existing exporters can do this:

  * node-exporter reads /sys/class/hwmon, which is empty inside WSL2 -- the
    kernel Docker Desktop runs exposes no thermal sensors at all, so
    node_hwmon_temp_celsius has zero series on a Windows host.
  * windows_exporter has no CPU core temperature collector. Its `thermalzone`
    collector reports ACPI zones, and on the box this was written for
    `\\_TZ.TZ00` sat at exactly 27.85 C with all sixteen cores pegged for 35
    seconds -- a board sensor, not the package. A dashboard fed from it draws a
    convincing flat line that means nothing.

So this walks a chain of providers and reports which one answered, because "no
sensor" and "a sensor reading 27.85" must not look the same on a dashboard.

    cpu_temperature_celsius{sensor="Core 0",source="hwmon"} 46.0
    cpu_temperature_source_info{source="hwmon"} 1
    cpu_temperature_available 1

Providers, in order of preference:

  hwmon   Linux /sys/class/hwmon -- coretemp (Intel) or k10temp (AMD). Works
          bare-metal and in a container with /sys mounted. Real DTS readings.
  lhm     LibreHardwareMonitor's JSON web server, for Windows hosts. LHM ships
          a signed kernel driver to read Intel DTS / AMD SMU, which is the only
          way to get true core temperature on Windows.
  acpi    Thermal zones, last resort, and only those that are the CPU's
          (x86_pkg_temp, cpu..., soc...), reported with source="acpi".

Every series is a temperature of the CPU itself, because the dashboards take
the hottest and the average of all of them: a board, a wifi card or a core's
distance to its throttle point would pass for a core.

Every series also has a name of its own. On a machine with two CPUs, both
sockets' chips are "coretemp" and each numbers its cores from 0; two series
with one name would reach Prometheus as one, and half the cores would be lost.
So a chip, an LHM CPU or a zone whose name repeats takes its place among its
namesakes: "coretemp.0/Core 0" and "coretemp.1/Core 0".

Environment:
    LHM_URL          default http://host.docker.internal:8085/data.json
    LISTEN_PORT      default 9110
    SCRAPE_TIMEOUT   default 4 (seconds, for the LHM HTTP call)
"""
from __future__ import annotations

import glob
import json
import os
import re
import socket
import sys
import time
import urllib.request
from collections import Counter
from http.server import BaseHTTPRequestHandler, HTTPServer

LHM_URL = os.environ.get("LHM_URL", "http://host.docker.internal:8085/data.json")
PORT = int(os.environ.get("LISTEN_PORT", "9110"))
TIMEOUT = float(os.environ.get("SCRAPE_TIMEOUT", "4"))

# Chips that expose real per-core CPU temperature. Anything else in hwmon is a
# drive, a chipset or a fan controller, and must not be labelled "CPU".
CPU_CHIPS = ("coretemp", "k10temp", "zenpower", "cpu_thermal", "k8temp")

# Thermal zones that are the CPU: Intel's package sensor, an ARM SoC's cpu zone,
# Intel DTT's TCPU. acpitz, a wifi card, the chipset or a battery are not.
CPU_ZONES = re.compile(r"x86_pkg_temp|cpu|soc", re.IGNORECASE)

# LibreHardwareMonitor sensors in degrees that are not a reading: how far a core
# is below its throttle point ("CPU Core #1 Distance to TjMax", 100 minus the
# core), and LHM's own max and average of the cores, which would count them twice.
NOT_A_READING = re.compile(r"distance to tjmax|\b(?:max|average)\b", re.IGNORECASE)


def _read(path: str) -> str | None:
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            return fh.read().strip()
    except OSError:
        return None


def _natural(path: str) -> list:
    """A sort key that puts hwmon2 before hwmon10."""
    return [int(t) if t.isdigit() else t for t in re.split(r"(\d+)", path)]


def _numbered(names: list[str], first: int = 0, sep: str = ".") -> list[str]:
    """Names that repeat take their place among their namesakes: two sockets'
    "coretemp" become "coretemp.0" and "coretemp.1". A name that does not
    repeat stays as it is, so a one-CPU machine keeps its series."""
    counts = Counter(names)
    seen: Counter = Counter()
    out = []
    for name in names:
        if name and counts[name] > 1:
            out.append(f"{name}{sep}{first + seen[name]}")
            seen[name] += 1
        else:
            out.append(name)
    return out


def from_hwmon(root: str = "/sys/class/hwmon") -> list[tuple[str, float]]:
    """Linux: /sys/class/hwmon/hwmonN/{name,tempN_input,tempN_label}. Chips in
    the order of the devices they are (coretemp.0, coretemp.1, or the PCI
    address of each socket's k10temp), which stays put across boots while the
    hwmon numbers may not."""
    chips = []
    for base in glob.glob(f"{root}/hwmon*"):
        chip = (_read(f"{base}/name") or "").lower()
        if any(c in chip for c in CPU_CHIPS):
            chips.append((_natural(os.path.realpath(f"{base}/device")), base, chip))
    chips.sort()
    out: list[tuple[str, float]] = []
    for (_, base, _chip), chip in zip(chips, _numbered([c for _, _, c in chips])):
        for inp in sorted(glob.glob(f"{base}/temp*_input"), key=_natural):
            raw = _read(inp)
            if raw is None:
                continue
            try:
                milli = float(raw)
            except ValueError:
                continue
            label = _read(inp.replace("_input", "_label")) or os.path.basename(inp)
            # hwmon reports millidegrees; a plausible CPU sits well inside 0-125 C.
            celsius = milli / 1000.0
            if not (0.0 < celsius < 150.0):
                continue
            out.append((f"{chip}/{label}", celsius))
    return out


def _lhm_sensors(node: dict, path: tuple[str, ...] = (), on_cpu: bool = False, text: str | None = None) -> list[tuple[tuple[str, ...], float, bool]]:
    """Every degree reading in LibreHardwareMonitor's /data.json, a tree of
    {Text, Value, ImageURL, SensorId, Children}: the names down to it (a name
    may hold a slash, "Core (Tctl/Tdie)"), its value, and whether it sits under
    the CPU itself rather than the board. Two of one name side by side (two
    CPUs of one model) are told apart by their place, as LHM numbers cores:
    "Intel Xeon Gold 6230 #1" and "#2"."""
    found: list[tuple[tuple[str, ...], float, bool]] = []
    text = text if text is not None else (node.get("Text") or "").strip()
    here = path + (text,) if text else path
    on_cpu = (on_cpu or str(node.get("ImageURL") or "").endswith("cpu.png")
              or str(node.get("SensorId") or "").startswith(("/intelcpu/", "/amdcpu/")))
    value = node.get("Value")
    if isinstance(value, str) and "°C" in value:
        # Values arrive as "46.0 °C".
        m = re.search(r"(-?\d+(?:[.,]\d+)?)", value)
        if m:
            found.append((here, float(m.group(1).replace(",", ".")), on_cpu))
    children = node.get("Children") or []
    names = _numbered([(c.get("Text") or "").strip() for c in children], first=1, sep=" #")
    for child, name in zip(children, names):
        found.extend(_lhm_sensors(child, here, on_cpu, name))
    return found


def _walk_lhm(node: dict) -> list[tuple[str, float]]:
    """The CPU's temperatures in LHM's tree: the CPU's own sensors (cores,
    package, dies) or, when LHM shows none, a board sensor named CPU (the
    socket, read by the Super I/O chip). A distance to TjMax and LHM's own max
    and average of the cores are not readings and are left out."""
    readings = [(p, v, cpu) for p, v, cpu in _lhm_sensors(node) if p and not NOT_A_READING.search(p[-1])]
    picked = [(p, v) for p, v, cpu in readings if cpu] or [(p, v) for p, v, _ in readings if "cpu" in p[-1].lower()]
    # The full LHM path is unusable as a legend -- it reads
    # "Sensor/HOSTNAME/MSI MPG Z690 CARBON WIFI (MS-7D30)/Nuvoton
    # NCT6687D/Temperatures/CPU". Keep the chip and the sensor, so
    # a board sensor is still distinguishable from the package.
    return [("/".join(x for x in p[-3:] if x != "Temperatures"), v) for p, v in picked]


def from_lhm() -> list[tuple[str, float]]:
    try:
        with urllib.request.urlopen(LHM_URL, timeout=TIMEOUT) as resp:
            data = json.loads(resp.read())
    except Exception:            # noqa: BLE001 - any failure just means "not this provider"
        return []
    return _walk_lhm(data)


def from_acpi(root: str = "/sys/class/thermal") -> list[tuple[str, float]]:
    """Last resort, labelled source="acpi": the thermal zones that are the CPU.
    acpitz is often a board sensor that never moves, and a wifi card or the
    chipset would pass for the hottest core."""
    zones = []
    for zone in sorted(glob.glob(f"{root}/thermal_zone*"), key=_natural):
        kind = _read(f"{zone}/type") or ""
        if CPU_ZONES.search(kind):
            zones.append((zone, kind))
    out: list[tuple[str, float]] = []
    # Two sockets have an x86_pkg_temp each.
    for (zone, _kind), name in zip(zones, _numbered([k for _, k in zones])):
        raw = _read(f"{zone}/temp")
        if raw is None:
            continue
        try:
            celsius = float(raw) / 1000.0
        except ValueError:
            continue
        if 0.0 < celsius < 150.0:
            out.append((name, celsius))
    return out


PROVIDERS = (("hwmon", from_hwmon), ("lhm", from_lhm), ("acpi", from_acpi))


def _without_offset_tctl(readings: list[tuple[str, float]]) -> list[tuple[str, float]]:
    """AMD's Tctl is the fan-control value: on the first Ryzens and Threadrippers
    it sits 10-27 C above the die, which they also report as Tdie. When a Tdie
    is there, a Tctl that is not also the Tdie ("Core (Tctl/Tdie)") goes."""
    tctl = re.compile(r"\bTctl\b")
    tdie = re.compile(r"\bTdie\b")
    if not any(tdie.search(n) and not tctl.search(n) for n, _ in readings):
        return readings
    return [(n, v) for n, v in readings if not tctl.search(n) or tdie.search(n)]


def _unique(readings: list[tuple[str, float]]) -> list[tuple[str, float]]:
    """Last guard: a name that still repeats (one chip labelling two sensors
    alike) gets " (2)", " (3)", since Prometheus would keep only one of them."""
    seen: Counter = Counter()
    out = []
    for name, value in readings:
        seen[name] += 1
        out.append((name if seen[name] == 1 else f"{name} ({seen[name]})", value))
    return out


def collect() -> tuple[str, list[tuple[str, float]]]:
    for name, fn in PROVIDERS:
        try:
            readings = _unique(_without_offset_tctl(fn()))
        except Exception:        # noqa: BLE001
            readings = []
        if readings:
            return name, readings
    return "none", []


def _esc(s: str) -> str:
    return s.replace("\\", "\\\\").replace('"', '\\"').replace("\n", " ")


def render() -> str:
    source, readings = collect()
    lines = [
        "# HELP cpu_temperature_celsius CPU temperature by sensor.",
        "# TYPE cpu_temperature_celsius gauge",
    ]
    for sensor, value in readings:
        lines.append(
            f'cpu_temperature_celsius{{sensor="{_esc(sensor)}",source="{source}"}} {value:.2f}')
    lines += [
        "# HELP cpu_temperature_available 1 when a real CPU sensor was found.",
        "# TYPE cpu_temperature_available gauge",
        f"cpu_temperature_available {1 if readings else 0}",
        "# HELP cpu_temperature_source_info Which provider answered.",
        "# TYPE cpu_temperature_source_info gauge",
        f'cpu_temperature_source_info{{source="{source}"}} 1',
        "# HELP cpu_temperature_max_celsius Hottest CPU sensor, for alerting.",
        "# TYPE cpu_temperature_max_celsius gauge",
    ]
    if readings:
        lines.append(f"cpu_temperature_max_celsius {max(v for _, v in readings):.2f}")
    return "\n".join(lines) + "\n"


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):                      # noqa: N802
        if self.path.split("?")[0] not in ("/metrics", "/"):
            self.send_error(404)
            return
        body = render().encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/plain; version=0.0.4; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_args):         # noqa: D102 - one line per scrape is noise
        pass


def main() -> int:
    source, readings = collect()
    print(f"cpu-temp-exporter: provider={source} sensors={len(readings)}", flush=True)
    if source == "none":
        # Deliberately not fatal. On a Windows host without LibreHardwareMonitor
        # there is no sensor to read, and a container that crash-loops would be
        # a worse failure mode than one that honestly reports
        # cpu_temperature_available 0 and lets the dashboard say so.
        print(f"cpu-temp-exporter: no CPU sensor found. On Windows, install "
              f"LibreHardwareMonitor and enable its web server, then set "
              f"LHM_URL (currently {LHM_URL}).", flush=True)
    HTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
