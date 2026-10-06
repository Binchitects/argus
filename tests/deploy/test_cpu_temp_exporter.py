"""deploy/services/cpu-temp-exporter/exporter.py: every series it reports is a temperature of the CPU itself, since the dashboards take the hottest and the average of all of them."""
from __future__ import annotations

import importlib.util
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("cpu_temp_exporter", REPO / "deploy/services/cpu-temp-exporter/exporter.py")
exporter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(exporter)


def sensor(text: str, value: str, sensor_id: str | None = None) -> dict:
    node = {"Text": text, "Min": value, "Value": value, "Max": value, "ImageURL": "images/transparent.png", "Children": []}
    if sensor_id:
        node["SensorId"] = sensor_id
        node["Type"] = "Temperature"
    return node


def group(text: str, image: str, *children: dict) -> dict:
    return {"Text": text, "Min": "", "Value": "", "Max": "", "ImageURL": image, "Children": list(children)}


def lhm_tree(cpu: dict | None) -> dict:
    """LibreHardwareMonitor's /data.json as it reads on a Z690 board: the board's Super I/O chip with its own
    "CPU" sensor, a GPU, a drive, and the CPU given (or none, as when LHM runs without its driver)."""
    board = group(
        "MSI MPG Z690 CARBON WIFI (MS-7D30)", "images_icon/mainboard.png",
        group(
            "Nuvoton NCT6687D", "images_icon/chip.png",
            group("Voltages", "images_icon/voltage.png", sensor("CPU", "1.240 V")),
            group("Temperatures", "images_icon/temperature.png", sensor("CPU", "65.0 °C"), sensor("System", "38.0 °C"), sensor("VRM MOS", "52.0 °C")),
        ),
    )
    gpu = group("NVIDIA GeForce RTX 4090", "images_icon/nvidia.png", group("Temperatures", "images_icon/temperature.png", sensor("GPU Core", "48.0 °C")))
    drive = group("Samsung SSD 990 PRO 2TB", "images_icon/hdd.png", group("Temperatures", "images_icon/temperature.png", sensor("Temperature", "41.0 °C")))
    host = group("HOST", "images_icon/computer.png", *([board] + ([cpu] if cpu else []) + [gpu, drive]))
    return group("Sensor", "", host)


def intel_cpu(name: str = "13th Gen Intel Core i7-13700K", index: int = 0, base: int = 40) -> dict:
    """An Intel CPU in LHM: per core a reading and its distance to TjMax (100 minus it), the package, and LHM's own max and average."""
    cores = [sensor(f"CPU Core #{i + 1}", f"{base + i},0 °C", f"/intelcpu/{index}/temperature/{i}") for i in range(4)]
    distances = [sensor(f"CPU Core #{i + 1} Distance to TjMax", f"{100 - base - i}.0 °C", f"/intelcpu/{index}/temperature/{i + 4}") for i in range(4)]
    temps = group(
        "Temperatures", "images_icon/temperature.png",
        *cores, sensor("CPU Package", f"{base + 5}.0 °C", f"/intelcpu/{index}/temperature/8"), *distances,
        sensor("Core Max", f"{base + 3}.0 °C", f"/intelcpu/{index}/temperature/9"), sensor("Core Average", f"{base + 1}.5 °C", f"/intelcpu/{index}/temperature/10"),
    )
    load = group("Load", "images_icon/load.png", sensor("CPU Total", "12.0 %"))
    return group(name, "images_icon/cpu.png", load, temps)


def amd_cpu(first_gen: bool) -> dict:
    """An AMD CPU in LHM, from an older LHM without SensorId: no sensor name says "CPU". A first Ryzen
    reports its offset Tctl beside the Tdie; a later one reports one Tctl/Tdie and a Tdie per CCD."""
    if first_gen:
        temps = [sensor("Core (Tctl)", "70.0 °C"), sensor("Core (Tdie)", "50.0 °C")]
    else:
        temps = [sensor("Core (Tctl/Tdie)", "61.0 °C"), sensor("CCD1 (Tdie)", "58.0 °C"), sensor("CCD2 (Tdie)", "55.0 °C"),
                 sensor("CCDs Max (Tdie)", "58.0 °C"), sensor("CCDs Average (Tdie)", "56.5 °C")]
    return group("AMD Ryzen 9 5950X" if not first_gen else "AMD Ryzen 7 1800X", "images_icon/cpu.png", group("Temperatures", "images_icon/temperature.png", *temps))


def sysfs(root: Path, files: dict[str, str]) -> str:
    for name, text in files.items():
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text + "\n", encoding="utf-8")
    return str(root)


class Lhm(unittest.TestCase):
    def test_an_intel_cpu_gives_its_cores_and_package_never_a_distance_to_tjmax_or_lhms_max_and_average(self):
        got = exporter._walk_lhm(lhm_tree(intel_cpu()))
        name = "13th Gen Intel Core i7-13700K"
        self.assertEqual(got, [(f"{name}/CPU Core #1", 40.0), (f"{name}/CPU Core #2", 41.0), (f"{name}/CPU Core #3", 42.0),
                               (f"{name}/CPU Core #4", 43.0), (f"{name}/CPU Package", 45.0)])

    def test_an_amd_cpu_is_found_by_where_it_sits_not_by_its_sensor_names(self):
        got = dict(exporter._walk_lhm(lhm_tree(amd_cpu(first_gen=False))))
        self.assertEqual(got, {"AMD Ryzen 9 5950X/Core (Tctl/Tdie)": 61.0, "AMD Ryzen 9 5950X/CCD1 (Tdie)": 58.0, "AMD Ryzen 9 5950X/CCD2 (Tdie)": 55.0})

    def test_without_the_cpu_the_board_sensor_named_cpu_stands_in(self):
        self.assertEqual(exporter._walk_lhm(lhm_tree(None)), [("Nuvoton NCT6687D/CPU", 65.0)])

    def test_a_tree_with_no_cpu_reading_gives_nothing(self):
        self.assertEqual(exporter._walk_lhm(group("Sensor", "", group("HOST", "images_icon/computer.png"))), [])

    def test_the_offset_tctl_of_a_first_ryzen_is_left_out(self):
        with mock.patch.object(exporter, "PROVIDERS", (("lhm", lambda: exporter._walk_lhm(lhm_tree(amd_cpu(first_gen=True)))),)):
            self.assertEqual(exporter.collect(), ("lhm", [("AMD Ryzen 7 1800X/Core (Tdie)", 50.0)]))

    def test_two_cpus_of_one_model_are_told_apart_by_their_place(self):
        xeon = "Intel Xeon Gold 6230"
        tree = group("Sensor", "", group("HOST", "images_icon/computer.png", intel_cpu(xeon, 0, 40), intel_cpu(xeon, 1, 60)))
        got = dict(exporter._walk_lhm(tree))
        self.assertEqual(len(got), 10)
        self.assertEqual(got[f"{xeon} #1/CPU Core #1"], 40.0)
        self.assertEqual(got[f"{xeon} #2/CPU Core #1"], 60.0)
        self.assertEqual(got[f"{xeon} #2/CPU Package"], 65.0)

    def test_the_metrics_have_the_cpus_hottest_sensor(self):
        with mock.patch.object(exporter, "PROVIDERS", (("lhm", lambda: exporter._walk_lhm(lhm_tree(intel_cpu()))),)):
            text = exporter.render()
        self.assertIn('cpu_temperature_celsius{sensor="13th Gen Intel Core i7-13700K/CPU Package",source="lhm"} 45.00', text)
        self.assertIn("cpu_temperature_max_celsius 45.00", text)
        self.assertNotIn("TjMax", text)
        self.assertNotIn("Nuvoton", text)


class Linux(unittest.TestCase):
    def test_hwmon_reads_only_cpu_chips_and_drops_an_offset_tctl(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = sysfs(Path(tmp), {
                "hwmon0/name": "nvme", "hwmon0/temp1_input": "45850", "hwmon0/temp1_label": "Composite",
                "hwmon1/name": "k10temp", "hwmon1/temp1_input": "70125", "hwmon1/temp1_label": "Tctl",
                "hwmon1/temp2_input": "50125", "hwmon1/temp2_label": "Tdie",
                "hwmon2/name": "iwlwifi_1", "hwmon2/temp1_input": "41000",
            })
            self.assertEqual(exporter._without_offset_tctl(exporter.from_hwmon(root)), [("k10temp/Tdie", 50.125)])

    def test_a_later_ryzen_keeps_its_tctl(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = sysfs(Path(tmp), {
                "hwmon3/name": "k10temp", "hwmon3/temp1_input": "61000", "hwmon3/temp1_label": "Tctl",
                "hwmon3/temp3_input": "58000", "hwmon3/temp3_label": "Tccd1",
            })
            self.assertEqual(exporter._without_offset_tctl(exporter.from_hwmon(root)), [("k10temp/Tctl", 61.0), ("k10temp/Tccd1", 58.0)])

    def test_two_sockets_of_one_chip_are_told_apart_in_the_order_of_their_devices(self):
        # Both sockets' chips are "coretemp" and both number their cores from 0. The hwmon numbers are not the
        # sockets' order (they may change across boots); the devices, coretemp.0 and coretemp.1, are.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            sysfs(root, {
                "devices/coretemp.0/x": "", "devices/coretemp.1/x": "",
                "hwmon/hwmon2/name": "coretemp", "hwmon/hwmon2/temp1_input": "61000", "hwmon/hwmon2/temp1_label": "Package id 1",
                "hwmon/hwmon2/temp2_input": "60000", "hwmon/hwmon2/temp2_label": "Core 0",
                "hwmon/hwmon10/name": "coretemp", "hwmon/hwmon10/temp1_input": "41000", "hwmon/hwmon10/temp1_label": "Package id 0",
                "hwmon/hwmon10/temp2_input": "40000", "hwmon/hwmon10/temp2_label": "Core 0",
                "hwmon/hwmon10/temp10_input": "42000", "hwmon/hwmon10/temp10_label": "Core 8",
                "hwmon/hwmon3/name": "nvme", "hwmon/hwmon3/temp1_input": "45850",
            })
            os.symlink(root / "devices/coretemp.1", root / "hwmon/hwmon2/device")
            os.symlink(root / "devices/coretemp.0", root / "hwmon/hwmon10/device")
            got = exporter.from_hwmon(str(root / "hwmon"))
        self.assertEqual(got, [("coretemp.0/Package id 0", 41.0), ("coretemp.0/Core 0", 40.0), ("coretemp.0/Core 8", 42.0),
                               ("coretemp.1/Package id 1", 61.0), ("coretemp.1/Core 0", 60.0)])
        # Every one a series of its own, so Prometheus keeps both sockets.
        with mock.patch.object(exporter, "PROVIDERS", (("hwmon", lambda: got),)):
            series = [l.rsplit(" ", 1)[0] for l in exporter.render().splitlines() if l.startswith("cpu_temperature_celsius{")]
        self.assertEqual(len(series), 5)
        self.assertEqual(len(set(series)), 5)

    def test_one_socket_keeps_the_names_it_had(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = sysfs(Path(tmp), {"hwmon1/name": "coretemp", "hwmon1/temp1_input": "41000", "hwmon1/temp1_label": "Package id 0",
                                     "hwmon1/temp2_input": "40000", "hwmon1/temp2_label": "Core 0"})
            self.assertEqual(exporter.from_hwmon(root), [("coretemp/Package id 0", 41.0), ("coretemp/Core 0", 40.0)])

    def test_a_name_that_still_repeats_is_made_unique(self):
        with mock.patch.object(exporter, "PROVIDERS", (("hwmon", lambda: [("k10temp/Tccd1", 50.0), ("k10temp/Tccd1", 52.0)]),)):
            self.assertEqual(exporter.collect(), ("hwmon", [("k10temp/Tccd1", 50.0), ("k10temp/Tccd1 (2)", 52.0)]))

    def test_acpi_keeps_the_zones_that_are_the_cpu(self):
        # This machine's zones: the board's acpitz, the wifi card, and Intel's package sensor.
        with tempfile.TemporaryDirectory() as tmp:
            root = sysfs(Path(tmp), {
                "thermal_zone0/type": "acpitz", "thermal_zone0/temp": "27800",
                "thermal_zone1/type": "iwlwifi_1", "thermal_zone1/temp": "41000",
                "thermal_zone2/type": "x86_pkg_temp", "thermal_zone2/temp": "81000",
                "thermal_zone3/type": "cpu-thermal", "thermal_zone3/temp": "55000",
                "thermal_zone4/type": "pch_cannonlake", "thermal_zone4/temp": "60000",
                "thermal_zone5/type": "TCPU", "thermal_zone5/temp": "79000",
            })
            self.assertEqual(exporter.from_acpi(root), [("x86_pkg_temp", 81.0), ("cpu-thermal", 55.0), ("TCPU", 79.0)])

    def test_acpi_tells_two_sockets_package_zones_apart(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = sysfs(Path(tmp), {
                "thermal_zone0/type": "acpitz", "thermal_zone0/temp": "27800",
                "thermal_zone10/type": "x86_pkg_temp", "thermal_zone10/temp": "62000",
                "thermal_zone2/type": "x86_pkg_temp", "thermal_zone2/temp": "81000",
            })
            self.assertEqual(exporter.from_acpi(root), [("x86_pkg_temp.0", 81.0), ("x86_pkg_temp.1", 62.0)])

    def test_acpi_with_only_a_board_zone_says_there_is_no_cpu_sensor(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = sysfs(Path(tmp), {"thermal_zone0/type": "acpitz", "thermal_zone0/temp": "27850"})
            self.assertEqual(exporter.from_acpi(root), [])
            with mock.patch.object(exporter, "PROVIDERS", (("acpi", lambda: exporter.from_acpi(root)),)):
                self.assertIn("cpu_temperature_available 0", exporter.render())


if __name__ == "__main__":
    unittest.main()
