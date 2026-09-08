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

# Read published ports so the summary matches whatever the user configured.
$envMap = @{}
foreach ($line in (Get-Content (Join-Path $root '.env'))) {
    if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $envMap[$matches[1]] = $matches[2].Trim() }
}
function Port($key, $default) {
    if ($envMap.ContainsKey($key) -and $envMap[$key]) { return $envMap[$key] }
    return $default
}

function EnvVal($key) {
    if ($envMap.ContainsKey($key)) { return $envMap[$key] }
    return ''
}

# Which engine is enabled. Both claim the whole GPU, so exactly one runs, and
# everything below follows the choice rather than assuming vLLM -- waiting on a
# hardcoded 'vllm' meant a llama.cpp deploy sat out the whole timeout against a
# container that was never going to exist.
$activeProfiles = $env:COMPOSE_PROFILES
if (-not $activeProfiles) { $activeProfiles = EnvVal 'COMPOSE_PROFILES' }
$engineContainer = 'vllm'
$engineLabel = 'vLLM'
if (",$activeProfiles," -like '*,llamacpp,*') {
    $engineContainer = 'llamacpp'
    $engineLabel = 'llama.cpp'
}

# Refuse to start an engine whose API key is still the placeholder from
# .env.example. Mirrors engine_key_guard in up.sh; the reasoning is there. In
# short: a Compose ${VAR:?} breaks every OTHER engine's deploy, and only checks
# that the variable EXISTS -- which the placeholder satisfies.
foreach ($pair in @(@('vllm', 'VLLM_API_KEY'), @('llamacpp', 'LLAMACPP_API_KEY'))) {
    if (",$activeProfiles," -notlike "*,$($pair[0]),*") { continue }
    $v = EnvVal $pair[1]
    if (-not $v) {
        Write-Host "refusing to start '$($pair[0])': $($pair[1]) is empty" -ForegroundColor Red
    } elseif ($v -like '*change-me*') {
        Write-Host "refusing to start '$($pair[0])': $($pair[1]) is still the placeholder from .env.example" -ForegroundColor Red
    } else {
        continue
    }
    Write-Host '  run .\scriptsootstrap.ps1 (or .\scripts\setup.ps1) to generate one' -ForegroundColor Yellow
    exit 1
}

Write-Host '==> Pulling images' -ForegroundColor Cyan
docker compose pull --quiet

Write-Host '==> Starting services' -ForegroundColor Cyan
docker compose up -d --remove-orphans
if ($LASTEXITCODE -ne 0) { exit 1 }


if (-not $NoWait) {
    Write-Host ''
    Write-Host "==> Waiting for $engineLabel to finish loading the model" -ForegroundColor Cyan
    Write-Host '    (first run downloads weights; this is the slow part)' -ForegroundColor DarkGray

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $ready = $false
    $spin = @('|', '/', '-', '\')
    $i = 0

    while ((Get-Date) -lt $deadline) {
        # No engine publishes a host port any more -- everything goes through
        # Traefik, and the API route needs auth. Docker's own healthcheck is
        # the authoritative signal and needs no credentials. This used to probe
        # localhost:VLLM_PORT, which could only ever time out.
        $health = docker inspect -f '{{.State.Health.Status}}' $engineContainer 2>$null
        if ($health -eq 'healthy') { $ready = $true; break }

        # Fail fast if the container died rather than waiting out the timeout.
        $state = docker inspect -f '{{.State.Status}}' $engineContainer 2>$null
        if ($state -eq 'exited') {
            Write-Host ''
            Write-Host "$engineLabel exited. Last 40 log lines:" -ForegroundColor Red
            docker logs --tail 40 $engineContainer
            exit 1
        }

        Write-Host "`r    $($spin[$i % 4]) waiting... " -NoNewline
        $i++
        Start-Sleep -Seconds 5
    }

    Write-Host "`r                          `r" -NoNewline
    if ($ready) {
        Write-Host "    $engineLabel is serving." -ForegroundColor Green
    } else {
        Write-Host "    Timed out after $TimeoutMinutes minutes. Check: docker logs -f $engineContainer" -ForegroundColor Yellow
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
