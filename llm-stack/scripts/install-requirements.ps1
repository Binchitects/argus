<#
.SYNOPSIS
    Install and verify everything the stack needs on a Windows host.

.DESCRIPTION
    Checks Docker, the NVIDIA driver, GPU passthrough into containers and Git
    Bash, then installs windows_exporter so Grafana can graph the REAL host's
    CPU, memory and temperature instead of the WSL2 VM's, and wires up the
    Prometheus scrape job.

    Installing a Windows service needs administrator rights. If this script is
    not already elevated it re-launches itself through UAC.

    Safe to re-run: everything is checked before it is installed.

.PARAMETER SkipHostExporter
    Skip windows_exporter entirely. Everything except host CPU temperature
    still works; no administrator rights are needed.

.EXAMPLE
    .\scripts\install-requirements.ps1
    .\scripts\install-requirements.ps1 -SkipHostExporter
#>
[CmdletBinding()]
param(
    [switch]$SkipHostExporter,
    [switch]$NoElevate,
    [int]$ExporterPort = 9182
)

# Native tools write progress to stderr; under 'Stop' that aborts the script.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "    OK   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "    !    $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "    X    $m" -ForegroundColor Red }

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

$issues = 0
$elevated = Test-Elevated

Write-Host ''
Write-Host '  LLMService - requirements' -ForegroundColor White
Write-Host '  -------------------------' -ForegroundColor DarkGray
Write-Host ''

# ---------------------------------------------------------------------------
# Re-launch through UAC if we need to install a service and cannot.
# ---------------------------------------------------------------------------
if (-not $SkipHostExporter -and -not $elevated -and -not $NoElevate) {
    Warn 'windows_exporter installs a Windows service, which needs administrator rights.'
    Warn 'Re-launching through UAC - approve the prompt.'
    Write-Host ''
    $argList = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-ExporterPort', $ExporterPort, '-NoElevate'
    )
    try {
        $p = Start-Process powershell.exe -ArgumentList $argList -Verb RunAs -Wait -PassThru
        exit $p.ExitCode
    } catch {
        Fail 'UAC was declined. Re-run from an elevated prompt, or use -SkipHostExporter.'
        exit 1
    }
}

# ---------------------------------------------------------------------------
Step 'Docker'
if (Get-Command docker -ErrorAction SilentlyContinue) {
    Ok (docker --version)
    $null = docker info 2>&1
    if ($LASTEXITCODE -ne 0) {
        Warn 'Docker daemon not responding - starting Docker Desktop'
        $dd = "$env:ProgramFiles\Docker\Docker\Docker Desktop.exe"
        if (Test-Path $dd) {
            Start-Process $dd
            $deadline = (Get-Date).AddMinutes(4)
            while ((Get-Date) -lt $deadline) {
                $null = docker info 2>&1
                if ($LASTEXITCODE -eq 0) { break }
                Start-Sleep -Seconds 5
            }
        }
        if ($LASTEXITCODE -eq 0) { Ok 'daemon up' } else { Fail 'daemon still down'; $issues++ }
    } else { Ok 'daemon reachable' }
} else {
    Fail 'Docker not installed: https://docker.com/products/docker-desktop'
    $issues++
}

# ---------------------------------------------------------------------------
Step 'Git Bash (needed by gen-auth / get-token / audit-auth)'
$gitBash = @(
    "$env:ProgramFiles\Git\bin\bash.exe",
    "${env:ProgramFiles(x86)}\Git\bin\bash.exe",
    "$env:LOCALAPPDATA\Programs\Git\bin\bash.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($gitBash) {
    Ok $gitBash
} else {
    Warn 'Git for Windows not found - installing'
    winget install --id Git.Git --source winget --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -eq 0) { Ok 'installed' } else { Fail 'install failed'; $issues++ }
}

# ---------------------------------------------------------------------------
Step 'NVIDIA GPU'
if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
    nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader | ForEach-Object { Ok $_ }
} else {
    Fail 'nvidia-smi not found - install the NVIDIA driver'
    $issues++
}

Step 'GPU passthrough into containers'
$gpu = docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi -L 2>&1
if ($LASTEXITCODE -eq 0) {
    ($gpu | Where-Object { $_ -match '^GPU \d+:' }) | ForEach-Object { Ok $_ }
} else {
    Fail 'Containers cannot see the GPU - enable WSL2 integration in Docker Desktop'
    $issues++
}

# ---------------------------------------------------------------------------
# windows_exporter is the ONLY source of real Windows host metrics.
# node-exporter runs inside the WSL2 VM and measures that VM - which has no
# thermal sensors, so CPU temperature is otherwise unavailable entirely.
# ---------------------------------------------------------------------------
if (-not $SkipHostExporter) {
    Step 'windows_exporter (real host CPU / memory / temperature)'
    $svc = Get-Service -Name windows_exporter -ErrorAction SilentlyContinue

    if (-not $svc) {
        $version = '0.31.8'
        $cache = Join-Path $root '.cache'
        New-Item -ItemType Directory -Path $cache -Force | Out-Null
        $msi = Join-Path $cache 'windows_exporter.msi'
        $url = "https://github.com/prometheus-community/windows_exporter/releases/download/v$version/windows_exporter-$version-amd64.msi"

        if (-not (Test-Path $msi) -or (Get-Item $msi).Length -lt 1MB) {
            Warn "downloading windows_exporter $version"
            # Three ways, because each fails differently on a locked-down host:
            #  1. Invoke-WebRequest  - .NET TLS, ignores schannel revocation woes
            #  2. curl --ssl-no-revoke - when the revocation server is offline
            #  3. a container        - when the host itself cannot reach GitHub
            $got = $false
            try {
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -Uri $url -OutFile $msi -TimeoutSec 180 -UseBasicParsing
                $got = (Test-Path $msi) -and (Get-Item $msi).Length -gt 1MB
            } catch { }

            if (-not $got -and (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
                & curl.exe -sL --ssl-no-revoke --retry 3 --retry-all-errors -o $msi $url
                $got = (Test-Path $msi) -and (Get-Item $msi).Length -gt 1MB
            }

            if (-not $got) {
                Warn 'host download failed - fetching through a container instead'
                $cacheUnix = '/' + $cache.Substring(0,1).ToLower() + $cache.Substring(2).Replace('\', '/')
                docker run --rm -v "${cache}:/out" alpine sh -c `
                    "apk add --no-cache curl >/dev/null 2>&1; curl -sL --retry 5 --retry-all-errors -o /out/windows_exporter.msi '$url'" | Out-Null
                $got = (Test-Path $msi) -and (Get-Item $msi).Length -gt 1MB
            }

            if ($got) { Ok ("downloaded {0:N1} MB" -f ((Get-Item $msi).Length / 1MB)) }
            else { Fail 'could not download windows_exporter'; $issues++ }
        } else {
            Ok 'installer already cached'
        }

        if (Test-Path $msi) {
            # thermalzone is NOT in the default collector set; it must be asked for.
            # NOTE: no 'cs' - that collector was removed in windows_exporter 0.31
            # and its presence makes the service exit at startup.
            $collectors = 'cpu,cpu_info,logical_disk,memory,net,os,system,thermalzone,service'
            $proc = Start-Process msiexec.exe -Wait -PassThru -ArgumentList @(
                '/i', "`"$msi`"", '/quiet', '/norestart',
                "ENABLED_COLLECTORS=$collectors", "LISTEN_PORT=$ExporterPort"
            )
            if ($proc.ExitCode -eq 0) {
                Ok 'installed'
            } elseif ($proc.ExitCode -eq 1603) {
                Fail 'msiexec 1603 - almost always missing administrator rights'
                $issues++
            } else {
                Fail "msiexec exit $($proc.ExitCode)"
                $issues++
            }
            Start-Sleep -Seconds 8
            $svc = Get-Service -Name windows_exporter -ErrorAction SilentlyContinue
        }
    }

    if ($svc) {
        # Repair a service installed with a stale collector list (e.g. 'cs',
        # removed in 0.31) - it would exit immediately on every start.
        $bin = (Get-CimInstance Win32_Service -Filter "Name='windows_exporter'").PathName
        if ($bin -match ',cs,') {
            Warn "existing service has an invalid collector list - repairing"
            $good = 'cpu,cpu_info,logical_disk,memory,net,os,system,thermalzone,service'
            $exe = 'C:\Program Files\windows_exporter\windows_exporter.exe'
            $cfg = 'C:\Program Files\windows_exporter\config.yaml'
            & sc.exe config windows_exporter binPath= "`"$exe`" --config.file=`"$cfg`" --collectors.enabled $good" | Out-Null
            Ok 'collector list corrected'
        }
        if ($svc.Status -ne 'Running') { Start-Service windows_exporter -ErrorAction SilentlyContinue; Start-Sleep -Seconds 4 }
        $svc.Refresh()
        Ok "service $($svc.Status)"
        try {
            $r = Invoke-WebRequest -Uri "http://localhost:$ExporterPort/metrics" -TimeoutSec 15 -UseBasicParsing
            $lines = $r.Content -split "`n"
            Ok "$($lines.Count) metrics on :$ExporterPort"
            $temp = @($lines | Where-Object { $_ -match '^windows_thermalzone_temperature_celsius\s' })
            if ($temp.Count -gt 0) {
                Ok "CPU temperature exposed - $($temp.Count) thermal zone(s)"
            } else {
                # Very common on consumer desktop boards.
                Warn 'thermalzone reports no sensors: this board does not publish CPU'
                Warn 'temperature through ACPI. GPU temperature is unaffected (driver-sourced).'
            }
        } catch {
            Fail "exporter not answering on :$ExporterPort"
            $issues++
        }
    }

    # ---- Prometheus scrape job ---------------------------------------------
    Step 'Prometheus scrape job for the host'
    $promFile = Join-Path $root 'config\prometheus\prometheus.yml'
    $prom = Get-Content $promFile -Raw -Encoding UTF8
    if ($prom -match '(?m)^\s*-\s*job_name:\s*windows\s*$') {
        Ok 'job already present'
    } else {
        $block = @"

  # ---- Windows host metrics (windows_exporter) -------------------------------
  # node-exporter measures the WSL2 VM; this measures the actual machine, and is
  # the only source of CPU temperature on Windows.
  - job_name: windows
    static_configs:
      - targets: ["host.docker.internal:$ExporterPort"]
        labels:
          tier: optional
          component: host
"@
        # UTF8 both ways: a default-encoding read followed by a UTF8 write
        # double-encodes any non-ASCII already in the file.
        [IO.File]::WriteAllText($promFile, $prom.TrimEnd() + "`n" + $block, [Text.UTF8Encoding]::new($false))
        Ok 'job added'
    }
    try { Invoke-WebRequest -Method Post -Uri 'http://127.0.0.1:9090/-/reload' -TimeoutSec 10 -UseBasicParsing | Out-Null; Ok 'Prometheus reloaded' } catch { Warn 'could not reload Prometheus (is it running?)' }
}

# ---------------------------------------------------------------------------
Write-Host ''
if ($issues -eq 0) {
    Write-Host '  All requirements satisfied.' -ForegroundColor Green
} else {
    Write-Host "  $issues problem(s) above need attention." -ForegroundColor Red
}
Write-Host ''
Write-Host '  Next:  .\scripts\up.ps1' -ForegroundColor DarkGray
Write-Host ''
if (-not $elevated -and -not $SkipHostExporter) { Read-Host '  Press Enter to close' | Out-Null }
exit $issues
