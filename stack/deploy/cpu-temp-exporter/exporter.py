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
  acpi    ACPI thermal zones, last resort, reported with source="acpi" so a
          panel can show that it is probably ambient rather than CPU.

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
from http.server import BaseHTTPRequestHandler, HTTPServer

LHM_URL = os.environ.get("LHM_URL", "http://host.docker.internal:8085/data.json")
PORT = int(os.environ.get("LISTEN_PORT", "9110"))
TIMEOUT = float(os.environ.get("SCRAPE_TIMEOUT", "4"))

# Chips that expose real per-core CPU temperature. Anything else in hwmon is a
# drive, a chipset or a fan controller, and must not be labelled "CPU".
CPU_CHIPS = ("coretemp", "k10temp", "zenpower", "cpu_thermal", "k8temp")


def _read(path: str) -> str | None:
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            return fh.read().strip()
    except OSError:
        return None


def from_hwmon() -> list[tuple[str, float]]:
    """Linux: /sys/class/hwmon/hwmonN/{name,tempN_input,tempN_label}."""
    out: list[tuple[str, float]] = []
    for base in sorted(glob.glob("/sys/class/hwmon/hwmon*")):
        chip = (_read(f"{base}/name") or "").lower()
        if not any(c in chip for c in CPU_CHIPS):
            continue
        for inp in sorted(glob.glob(f"{base}/temp*_input")):
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


def _walk_lhm(node: dict, path: str = "") -> list[tuple[str, float]]:
    """LibreHardwareMonitor's /data.json is a tree of {Text, Value, Children}."""
    found: list[tuple[str, float]] = []
    text = (node.get("Text") or "").strip()
    here = f"{path}/{text}" if text else path
    value = node.get("Value")
    if isinstance(value, str) and "°C" in value:
        # Values arrive as "46.0 °C"; a CPU node anywhere up the path qualifies it.
        m = re.search(r"(-?\d+(?:[.,]\d+)?)", value)
        if m and "cpu" in here.lower():
            try:
                # The full LHM path is unusable as a legend -- it reads
                # "Sensor/HOSTNAME/MSI MPG Z690 CARBON WIFI (MS-7D30)/Nuvoton
                # NCT6687D/Temperatures/CPU". Keep the chip and the sensor, so
                # a board sensor is still distinguishable from the package.
                parts = [x for x in here.strip("/").split("/") if x]
                short = "/".join(parts[-3:-1] + parts[-1:]) if len(parts) >= 3 else here
                short = short.replace("Temperatures/", "")
                found.append((short, float(m.group(1).replace(",", "."))))
            except ValueError:
                pass
    for child in node.get("Children") or []:
        found.extend(_walk_lhm(child, here))
    return found


def from_lhm() -> list[tuple[str, float]]:
    try:
        with urllib.request.urlopen(LHM_URL, timeout=TIMEOUT) as resp:
            data = json.loads(resp.read())
    except Exception:            # noqa: BLE001 - any failure just means "not this provider"
        return []
    return _walk_lhm(data)


def from_acpi() -> list[tuple[str, float]]:
    """Last resort. Often an ambient/board sensor rather than the CPU, which is
    why it is labelled source="acpi" instead of being silently mixed in."""
    out: list[tuple[str, float]] = []
    for zone in sorted(glob.glob("/sys/class/thermal/thermal_zone*")):
        raw = _read(f"{zone}/temp")
        if raw is None:
            continue
        try:
            celsius = float(raw) / 1000.0
        except ValueError:
            continue
        if 0.0 < celsius < 150.0:
            kind = _read(f"{zone}/type") or os.path.basename(zone)
            out.append((kind, celsius))
    return out


PROVIDERS = (("hwmon", from_hwmon), ("lhm", from_lhm), ("acpi", from_acpi))


def collect() -> tuple[str, list[tuple[str, float]]]:
    for name, fn in PROVIDERS:
        try:
            readings = fn()
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
