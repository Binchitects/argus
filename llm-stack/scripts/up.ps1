<#
.SYNOPSIS
    Start the stack and wait until vLLM is actually serving.

.PARAMETER Profiles
    Override COMPOSE_PROFILES for this run, e.g. -Profiles "smi,logging,gateway".

.PARAMETER NoWait
    Return as soon as containers are created instead of waiting for the model.

.EXAMPLE
    .\scripts\up.ps1
    .\scripts\up.ps1 -Profiles "smi,logging,tracing"
#>
[CmdletBinding()]
param(
    [string]$Profiles,
    [switch]$NoWait,
    [int]$TimeoutMinutes = 30
)

# Native CLIs write progress and warnings to stderr. Under 'Stop', PowerShell
# turns any native stderr line into a terminating NativeCommandError, so
# docker's ordinary output would abort the script. Check $LASTEXITCODE instead.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Test-Path (Join-Path $root '.env'))) {
    Write-Host 'No .env found. Running bootstrap first...' -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'bootstrap.ps1')
}

if ($Profiles) {
    $env:COMPOSE_PROFILES = $Profiles
    Write-Host "Using profiles: $Profiles" -ForegroundColor Cyan
}

Write-Host '==> Pulling images' -ForegroundColor Cyan
docker compose pull --quiet

Write-Host '==> Starting services' -ForegroundColor Cyan
docker compose up -d --remove-orphans
if ($LASTEXITCODE -ne 0) { exit 1 }

# Read published ports so the summary matches whatever the user configured.
$envMap = @{}
foreach ($line in (Get-Content (Join-Path $root '.env'))) {
    if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $envMap[$matches[1]] = $matches[2].Trim() }
}
function Port($key, $default) {
    if ($envMap.ContainsKey($key) -and $envMap[$key]) { return $envMap[$key] }
    return $default
}

if (-not $NoWait) {
    Write-Host ''
    Write-Host '==> Waiting for vLLM to finish loading the model' -ForegroundColor Cyan
    Write-Host '    (first run downloads weights; this is the slow part)' -ForegroundColor DarkGray

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $url = "http://localhost:$(Port 'VLLM_PORT' '8000')/health"
    $ready = $false
    $spin = @('|', '/', '-', '\')
    $i = 0

    while ((Get-Date) -lt $deadline) {
        try {
            $resp = Invoke-WebRequest -Uri $url -TimeoutSec 5 -UseBasicParsing
            if ($resp.StatusCode -eq 200) { $ready = $true; break }
        } catch {
            # Not up yet — expected for most of this loop.
        }

        # Fail fast if the container died rather than waiting out the timeout.
        $state = docker inspect -f '{{.State.Status}}' vllm 2>$null
        if ($state -eq 'exited') {
            Write-Host ''
            Write-Host 'vLLM exited. Last 40 log lines:' -ForegroundColor Red
            docker logs --tail 40 vllm
            exit 1
        }

        Write-Host "`r    $($spin[$i % 4]) waiting... " -NoNewline
        $i++
        Start-Sleep -Seconds 5
    }

    Write-Host "`r                          `r" -NoNewline
    if ($ready) {
        Write-Host '    vLLM is serving.' -ForegroundColor Green
    } else {
        Write-Host "    Timed out after $TimeoutMinutes minutes. Check: docker logs -f vllm" -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host '  Endpoints' -ForegroundColor White
Write-Host '  ---------' -ForegroundColor DarkGray
Write-Host "  Landing page  http://localhost:$(Port 'HOMEPAGE_PORT' '3003')"
Write-Host "  Chat UI       http://localhost:$(Port 'OPENWEBUI_PORT' '3000')"
Write-Host "  vLLM API      http://localhost:$(Port 'VLLM_PORT' '8000')/v1"
Write-Host "  vLLM docs     http://localhost:$(Port 'VLLM_PORT' '8000')/docs"
Write-Host "  Grafana       http://localhost:$(Port 'GRAFANA_PORT' '3001')"
Write-Host "  Prometheus    http://localhost:$(Port 'PROMETHEUS_PORT' '9090')"
Write-Host "  Alertmanager  http://localhost:$(Port 'ALERTMANAGER_PORT' '9093')"
if ($envMap['COMPOSE_PROFILES'] -match 'tracing') {
    Write-Host "  Langfuse      http://localhost:$(Port 'LANGFUSE_PORT' '3002')"
}
if ($envMap['COMPOSE_PROFILES'] -match 'gateway') {
    Write-Host "  LiteLLM       http://localhost:$(Port 'LITELLM_PORT' '4000')"
}
Write-Host ''
Write-Host '  Verify with:  .\scripts\smoke-test.ps1' -ForegroundColor DarkGray
Write-Host ''
