# CPU temperature


Works on Linux with no setup. Windows needs one host agent, because Windows
does not expose CPU temperature to userland at all.

## Why the stack could not already do this

Both existing exporters were checked before writing a new one:

| exporter | result |
|---|---|
| node-exporter | `node_hwmon_temp_celsius` has **0 series** on a Windows host. WSL2's kernel exposes no thermal sensors -- `/sys/class/hwmon/` is empty and there are no thermal zones, only cooling devices. |
| windows_exporter | Has **no CPU core temperature collector**. Its `thermalzone` collector reports ACPI zones. |

The ACPI zone deserves its own warning. On the machine this was written for,
`\_TZ.TZ00` read **27.85 C at idle and 27.85 C with all sixteen cores saturated
for 35 seconds** -- a delta of exactly 0.00. It is a board sensor that happens
to sit in the thermal-zone namespace. A dashboard fed from it draws a
convincing flat line that means nothing, which is worse than an empty panel.

## What it does now

`deploy/services/cpu-temp-exporter/exporter.py` walks a chain of providers and reports
which one answered:

| source | where | notes |
|---|---|---|
| `hwmon` | Linux `/sys/class/hwmon` | coretemp (Intel) / k10temp (AMD). Real DTS. Works with no setup -- a container's `/sys` already is the host's. |
| `lhm` | LibreHardwareMonitor's JSON server | Windows. LHM ships the kernel driver that reads Intel DTS. |
| `acpi` | `/sys/class/thermal` | Last resort, labelled `source="acpi"`, and only the zones that are the CPU: `x86_pkg_temp`, `cpu...`, `soc...`. |

Every series it reports is a temperature of the CPU itself, because the
dashboards take the hottest and the average of all of them. So it leaves out:

- LHM's **Distance to TjMax** sensors (how far a core is below its throttle
  point, about 100 minus the core) and LHM's own **Core Max** and **Core
  Average**, which would count the cores twice.
- The board's `CPU` sensor (its Super I/O chip's, such as `Nuvoton
  NCT6687D/CPU`) whenever LHM shows the CPU's own sensors. It stands in only
  when the CPU's are missing.
- AMD's `Tctl` when the chip also reports `Tdie`: on the first Ryzens and
  Threadrippers Tctl runs 10-27 C above the die, for the fans.
- Thermal zones that are not the CPU: `acpitz` (the board, see above), a wifi
  card, the chipset, a battery.

Every series has a name of its own. On a machine with two CPUs both sockets'
chips are `coretemp` (or `k10temp`), and each numbers its cores from 0. Two
series with one name would reach Prometheus as one, and half the cores would
be lost. So a name that repeats takes its place among its namesakes:

- hwmon: `coretemp.0/Core 0` and `coretemp.1/Core 0`, in the order of the
  chips' devices, which stays the same across reboots.
- LHM: two CPUs of one model become `Intel Xeon Gold 6230 #1/CPU Core #1` and
  `Intel Xeon Gold 6230 #2/CPU Core #1`, numbered as LHM numbers cores.
- acpi: two sockets' package zones become `x86_pkg_temp.0` and `x86_pkg_temp.1`.

A machine with one CPU keeps the names it had.

Metrics:

```
cpu_temperature_celsius{sensor="13th Gen Intel Core i7-13700K/CPU Package",source="lhm"} 50.00
cpu_temperature_max_celsius 50.00
cpu_temperature_available 1
cpu_temperature_source_info{source="lhm"} 1
```

`cpu_temperature_available` exists so that **no sensor** and **a sensor reading
27.85** cannot look the same. When there is no sensor the dashboard panels say
"No CPU sensor" rather than plotting nothing and looking broken.

The dashboards (Resources, Stack Performance) draw two lines, the hottest sensor
and the average of all of them, beside other panels: an Intel CPU reports one
sensor per core and one for the package, so a line each there would make the
chart, and the row it sits in, grow with the core count. A gap between the two
lines is one core or one die running hot. To see which, **CPU temperature per
sensor** on Resources has a line per sensor across the whole width (the seven
hottest, the rest averaged as Other; **Show as table** lists every sensor).

## Linux

Nothing to do. Enable the `smi` profile and the exporter reads coretemp
directly.

## Windows

Install LibreHardwareMonitor and turn on its web server:

1. Download the release zip from
   <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases>
2. Extract it, then either tick **Options -> Remote Web Server -> Run**, or
   write `LibreHardwareMonitor.config` next to the exe before first launch:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <appSettings>
       <add key="listenerPort" value="8085" />
       <add key="runWebServerMenuItem" value="true" />
       <add key="minTrayMenuItem" value="true" />
       <add key="startMinMenuItem" value="true" />
     </appSettings>
   </configuration>
   ```

3. **Run it as Administrator.** The kernel driver that reads Intel DTS will not
   load otherwise, and LHM silently reports no CPU sensors instead of failing.
4. To survive a reboot it needs a scheduled task, because a Run key cannot
   elevate. From an **elevated** PowerShell:

   ```powershell
   $exe = "$env:LOCALAPPDATA\LibreHardwareMonitor\LibreHardwareMonitor.exe"
   Register-ScheduledTask -TaskName "LibreHardwareMonitor" -Force `
     -Action (New-ScheduledTaskAction -Execute $exe) `
     -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME) `
     -Principal (New-ScheduledTaskPrincipal -UserId $env:USERNAME `
                   -LogonType Interactive -RunLevel Highest)
   ```

Point the exporter elsewhere with `LHM_URL` if LHM runs on another host or port.

## Verifying it is a real sensor

Do not trust a temperature that has never moved. Load every core and watch it:

```bash
docker run --rm -d --name burn --cpus 16 alpine \
  sh -c 'for i in $(seq 1 16); do (while :; do :; done) & done; sleep 60'
```

Measured this way on an i7-13700K:

| sensor | idle | full load | delta |
|---|---|---|---|
| i7-13700K CPU Package | 59 C | 100 C | **+41** |
| Nuvoton NCT6687D/CPU | 65 C | 99 C | +34 |
| ACPI `\_TZ.TZ00` (rejected) | 27.85 C | 27.85 C | **+0.00** |

A sensor that does not move under full load is not measuring your CPU.
