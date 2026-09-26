<#
.SYNOPSIS
    PowerShell wrapper for scripts/health.sh.

.DESCRIPTION
    The logic lives in the bash script so there is one implementation to keep
    correct. This wrapper locates the Git for Windows bash and forwards all
    arguments unchanged.

    It used to probe http://localhost:<port> directly. That stopped working the
    moment the stack moved behind Traefik: only 80/443 are published now, so
    every probe failed and a perfectly healthy stack was reported as entirely
    down. health.sh probes from INSIDE the docker network instead, which is
    what the README always claimed this did.
#>
[CmdletBinding()]
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot

# Look for Git's bash FIRST. `Get-Command bash` on Windows usually resolves to
# WSL's bash in System32, which mounts drives at /mnt/e - so an MSYS path like
# /e/Projects/... fails there with "No such file or directory".
$bash = @(
    "$env:ProgramFiles\Git\bin\bash.exe",
    "${env:ProgramFiles(x86)}\Git\bin\bash.exe",
    "$env:LOCALAPPDATA\Programs\Git\bin\bash.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $bash) {
    $candidate = (Get-Command bash -ErrorAction SilentlyContinue).Source
    if ($candidate -and $candidate -notlike "*System32*") { $bash = $candidate }
}
if (-not $bash) {
    Write-Host 'Git Bash not found. Install Git for Windows (it ships bash).' -ForegroundColor Red
    exit 1
}

# Git Bash wants an MSYS path (/e/foo), not a Windows one (E:/foo).
$unix = '/' + $root.Substring(0,1).ToLower() + $root.Substring(2).Replace('\', '/')
& $bash "$unix/scripts/health.sh" @Rest
exit $LASTEXITCODE
