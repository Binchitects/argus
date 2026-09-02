<#
.SYNOPSIS
    Stop the stack.

.DESCRIPTION
    By default containers are removed but named volumes are kept, so the model
    cache, metrics history and chat history all survive. -Purge deletes them.

.EXAMPLE
    .\scripts\down.ps1
    .\scripts\down.ps1 -Purge
#>
[CmdletBinding()]
param(
    [switch]$Purge,
    [switch]$KeepModelCache
)

# Native CLIs write progress and warnings to stderr. Under 'Stop', PowerShell
# turns any native stderr line into a terminating NativeCommandError, so
# docker's ordinary output would abort the script. Check $LASTEXITCODE instead.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# --profile "*" makes `down` reach containers from profiles that are not
# currently enabled; without it, disabling a profile orphans its containers.
if ($Purge) {
    Write-Host 'This deletes ALL volumes: models, metrics, dashboards, chat history.' -ForegroundColor Red
    $answer = Read-Host 'Type PURGE to confirm'
    if ($answer -ne 'PURGE') { Write-Host 'Aborted.'; exit 1 }

    if ($KeepModelCache) {
        docker compose --profile "*" down --remove-orphans
        $project = 'llmservice'
        foreach ($line in (Get-Content '.env' -ErrorAction SilentlyContinue)) {
            if ($line -match '^\s*COMPOSE_PROJECT_NAME=(.*)$') { $project = $matches[1].Trim() }
        }
        $keep = @("${project}_hf-cache", "${project}_vllm-cache")
        $all = docker volume ls -q --filter "name=${project}_"
        foreach ($v in $all) {
            if ($keep -contains $v) {
                Write-Host "  keeping  $v" -ForegroundColor DarkGray
            } else {
                docker volume rm $v | Out-Null
                Write-Host "  removed  $v" -ForegroundColor Yellow
            }
        }
    } else {
        docker compose --profile "*" down --volumes --remove-orphans
    }
} else {
    docker compose --profile "*" down --remove-orphans
    Write-Host ''
    Write-Host 'Volumes preserved. Restart with: .\scripts\up.ps1' -ForegroundColor DarkGray
}
