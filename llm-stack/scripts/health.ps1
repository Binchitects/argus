<#
.SYNOPSIS
    Check every component of the stack and print a status table.

.DESCRIPTION
    Distinguishes three states:
      OK       responding as expected
      DOWN     should be running (profile enabled) but is not answering
      SKIP     the owning compose profile is not enabled

.EXAMPLE
    .\scripts\health.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$envMap = @{}
if (Test-Path '.env') {
    foreach ($line in (Get-Content '.env')) {
        if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $envMap[$matches[1]] = $matches[2].Trim() }
    }
}
function Port($key, $default) {
    if ($envMap.ContainsKey($key) -and $envMap[$key]) { return $envMap[$key] }
    return $default
}
$profiles = $envMap['COMPOSE_PROFILES']
if (-not $profiles) { $profiles = '' }

# name, url, required-profile ('' = always on), expected-status
$checks = @(
    @{ Name = 'vLLM /health';       Url = "http://localhost:$(Port 'VLLM_PORT' '8000')/health";        Profile = '' }
    @{ Name = 'vLLM /metrics';      Url = "http://localhost:$(Port 'VLLM_PORT' '8000')/metrics";       Profile = '' }
    @{ Name = 'Open WebUI';         Url = "http://localhost:$(Port 'OPENWEBUI_PORT' '3000')/health";   Profile = '' }
    @{ Name = 'Prometheus';         Url = "http://localhost:$(Port 'PROMETHEUS_PORT' '9090')/-/healthy"; Profile = '' }
    @{ Name = 'Alertmanager';       Url = "http://localhost:$(Port 'ALERTMANAGER_PORT' '9093')/-/healthy"; Profile = '' }
    @{ Name = 'Grafana';            Url = "http://localhost:$(Port 'GRAFANA_PORT' '3001')/api/health"; Profile = '' }
    @{ Name = 'node-exporter';      Url = "http://localhost:$(Port 'NODE_EXPORTER_PORT' '9100')/metrics"; Profile = '' }
    @{ Name = 'cAdvisor';           Url = "http://localhost:$(Port 'CADVISOR_PORT' '8081')/healthz";   Profile = '' }
    @{ Name = 'GPU exporter (smi)'; Url = "http://localhost:$(Port 'NVIDIA_SMI_EXPORTER_PORT' '9835')/metrics"; Profile = 'smi' }
    @{ Name = 'GPU exporter (dcgm)';Url = "http://localhost:$(Port 'DCGM_EXPORTER_PORT' '9400')/metrics"; Profile = 'dcgm' }
    @{ Name = 'Loki';               Url = "http://localhost:$(Port 'LOKI_PORT' '3100')/ready";         Profile = 'logging' }
    @{ Name = 'LiteLLM';            Url = "http://localhost:$(Port 'LITELLM_PORT' '4000')/health/liveliness"; Profile = 'gateway' }
    @{ Name = 'Langfuse';           Url = "http://localhost:$(Port 'LANGFUSE_PORT' '3002')/api/public/health"; Profile = 'tracing' }
    @{ Name = 'Homepage';           Url = "http://localhost:$(Port 'HOMEPAGE_PORT' '3003')";           Profile = 'homepage' }
    @{ Name = 'Traefik ping';       Url = "http://localhost:$(Port 'TRAEFIK_METRICS_PORT' '8082')/ping";    Profile = 'proxy' }
    @{ Name = 'Traefik metrics';    Url = "http://localhost:$(Port 'TRAEFIK_METRICS_PORT' '8082')/metrics"; Profile = 'proxy' }
)

Write-Host ''
Write-Host ('  {0,-24} {1,-8} {2}' -f 'COMPONENT', 'STATUS', 'DETAIL') -ForegroundColor White
Write-Host ('  ' + ('-' * 70)) -ForegroundColor DarkGray

$failures = 0
foreach ($c in $checks) {
    if ($c.Profile -and ($profiles -notmatch [regex]::Escape($c.Profile))) {
        Write-Host ('  {0,-24} ' -f $c.Name) -NoNewline
        Write-Host ('{0,-8} ' -f 'SKIP') -NoNewline -ForegroundColor DarkGray
        Write-Host "profile '$($c.Profile)' not enabled" -ForegroundColor DarkGray
        continue
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = Invoke-WebRequest -Uri $c.Url -TimeoutSec 8 -UseBasicParsing
        $sw.Stop()
        Write-Host ('  {0,-24} ' -f $c.Name) -NoNewline
        Write-Host ('{0,-8} ' -f 'OK') -NoNewline -ForegroundColor Green
        Write-Host ("HTTP {0} in {1} ms" -f $resp.StatusCode, $sw.ElapsedMilliseconds) -ForegroundColor DarkGray
    } catch {
        $sw.Stop()
        $failures++
        Write-Host ('  {0,-24} ' -f $c.Name) -NoNewline
        Write-Host ('{0,-8} ' -f 'DOWN') -NoNewline -ForegroundColor Red
        Write-Host $_.Exception.Message.Split("`n")[0] -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------------------
# Prometheus' own view of the world is the authoritative one — it sees the
# containers on the internal network, not just the published ports.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '  Prometheus scrape targets' -ForegroundColor White
Write-Host ('  ' + ('-' * 70)) -ForegroundColor DarkGray
try {
    $targets = Invoke-RestMethod -Uri "http://localhost:$(Port 'PROMETHEUS_PORT' '9090')/api/v1/targets" -TimeoutSec 8
    foreach ($t in ($targets.data.activeTargets | Sort-Object { $_.labels.job })) {
        $color = 'Green'
        if ($t.health -ne 'up') {
            if ($t.labels.tier -eq 'optional') { $color = 'DarkGray' } else { $color = 'Red' }
        }
        Write-Host ('  {0,-24} {1,-8} {2}' -f $t.labels.job, $t.health.ToUpper(), $t.scrapeUrl) -ForegroundColor $color
    }
} catch {
    Write-Host '  (Prometheus API unreachable)' -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '  Containers' -ForegroundColor White
Write-Host ('  ' + ('-' * 70)) -ForegroundColor DarkGray
docker compose ps --format 'table {{.Name}}\t{{.Status}}' | ForEach-Object { Write-Host "  $_" }

Write-Host ''
if ($failures -eq 0) {
    Write-Host '  All enabled components healthy.' -ForegroundColor Green
} else {
    Write-Host "  $failures component(s) not responding." -ForegroundColor Red
}
Write-Host ''
exit $failures
