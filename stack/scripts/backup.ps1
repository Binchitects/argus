<#
.SYNOPSIS
    Back up (or restore) the stateful Docker volumes.

.DESCRIPTION
    The model cache is deliberately excluded — it is large and re-downloadable.
    What is NOT re-creatable is your chat history, dashboards you customised in
    Grafana, metrics history, and the gateway/tracing databases.

.EXAMPLE
    .\scripts\backup.ps1
    .\scripts\backup.ps1 -Restore -From .\backups\2026-08-18_141230
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [switch]$Restore,
    [string]$From,
    [switch]$IncludeModelCache
)

# Native CLIs write progress and warnings to stderr. Under 'Stop', PowerShell
# turns any native stderr line into a terminating NativeCommandError, so
# docker's ordinary output would abort the script. Check $LASTEXITCODE instead.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$project = 'llmservice'
$envLines = Get-Content '.env' -ErrorAction SilentlyContinue
foreach ($line in $envLines) {
    if ($line -match '^\s*COMPOSE_PROJECT_NAME=(.*)$') { $project = $matches[1].Trim() }
}

$volumes = @(
    'open-webui-data',
    'grafana-data',
    'prometheus-data',
    'alertmanager-data',
    'postgres-data',
    'loki-data',
    'clickhouse-data',
    'minio-data'
)
if ($IncludeModelCache) { $volumes += 'hf-cache' }

# ---------------------------------------------------------------------------
if ($Restore) {
    if (-not $From -or -not (Test-Path $From)) {
        Write-Host 'Pass -From <backup directory>' -ForegroundColor Red
        exit 1
    }

    Write-Host 'Restoring will OVERWRITE current volume contents.' -ForegroundColor Yellow
    Write-Host 'Stop the stack first if it is running: docker compose down' -ForegroundColor Yellow
    $answer = Read-Host 'Type RESTORE to continue'
    if ($answer -ne 'RESTORE') { Write-Host 'Aborted.'; exit 1 }

    foreach ($v in $volumes) {
        $archive = Join-Path (Resolve-Path $From) "$v.tar.gz"
        if (-not (Test-Path $archive)) {
            Write-Host "  skip  $v (no archive)" -ForegroundColor DarkGray
            continue
        }
        $full = "${project}_$v"
        docker volume create $full | Out-Null
        docker run --rm `
            -v "${full}:/target" `
            -v "$((Resolve-Path $From).Path):/backup:ro" `
            alpine sh -c "rm -rf /target/* /target/..?* /target/.[!.]* 2>/dev/null; tar xzf /backup/$v.tar.gz -C /target"
        Write-Host "  restored  $v" -ForegroundColor Green
    }
    Write-Host ''
    Write-Host 'Done. Start with: .\scripts\up.ps1' -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------------------
if (-not $OutputDir) {
    $OutputDir = Join-Path $root ('backups\' + (Get-Date -Format 'yyyy-MM-dd_HHmmss'))
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$outFull = (Resolve-Path $OutputDir).Path

Write-Host ''
Write-Host "  Backing up to $outFull" -ForegroundColor Cyan
Write-Host ''

foreach ($v in $volumes) {
    $full = "${project}_$v"
    $null = docker volume inspect $full 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("  {0,-20} skip (volume does not exist)" -f $v) -ForegroundColor DarkGray
        continue
    }

    # A throwaway alpine container is the portable way to read a named volume;
    # the volume's real path on disk is inside the WSL2 VM on Windows.
    docker run --rm `
        -v "${full}:/source:ro" `
        -v "${outFull}:/backup" `
        alpine tar czf "/backup/$v.tar.gz" -C /source . 2>$null

    if (Test-Path (Join-Path $outFull "$v.tar.gz")) {
        $size = (Get-Item (Join-Path $outFull "$v.tar.gz")).Length / 1MB
        Write-Host ("  {0,-20} {1,8:N1} MB" -f $v, $size) -ForegroundColor Green
    } else {
        Write-Host ("  {0,-20} FAILED" -f $v) -ForegroundColor Red
    }
}

# Config and .env are the other half of a restore.
Copy-Item -Path (Join-Path $root '.env') -Destination $outFull -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $root 'config') -Destination $outFull -Recurse -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "  Backup complete: $outFull" -ForegroundColor Green
Write-Host '  Note: .env contains secrets — store this directory securely.' -ForegroundColor Yellow
Write-Host ''
