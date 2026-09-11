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

`deploy/cpu-temp-exporter/exporter.py` walks a chain of providers and reports
which one answered:

| source | where | notes |
|---|---|---|
| `hwmon` | Linux `/sys/class/hwmon` | coretemp (Intel) / k10temp (AMD). Real DTS. Works with no setup -- a container's `/sys` already is the host's. |
| `lhm` | LibreHardwareMonitor's JSON server | Windows. LHM ships the kernel driver that reads Intel DTS. |
| `acpi` | `/sys/class/thermal` | Last resort, labelled `source="acpi"` so a panel can show it is probably ambient. |

Metrics:

```
cpu_temperature_celsius{sensor="13th Gen Intel Core i7-13700K/CPU Package",source="lhm"} 50.00
cpu_temperature_max_celsius 50.00
cpu_temperature_available 1
cpu_temperature_source_info{source="lhm"} 1
```

`cpu_temperature_available` exists so that **no sensor** and **a sensor reading
27.85** cannot look the same. When it is 0 the Grafana panel says "No CPU
sensor available on this host" rather than plotting nothing and looking broken.

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
