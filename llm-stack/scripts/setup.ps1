<#
.SYNOPSIS
    Guided end-to-end setup for the LLMService stack (Windows).

.DESCRIPTION
    Delegates to scripts/setup.sh through Git Bash, the same way the other
    .ps1 wrappers in this directory do, so there is one implementation of the
    setup logic rather than two that drift.

    MSYS_NO_PATHCONV is exported before handing over. Git Bash otherwise
    rewrites any argument that looks like a path, which turns openssl's
    -subj "/CN=..." into "C:/Program Files/Git/CN=..." and makes certificate
    generation fail in a way that reports a corrupt CA rather than a mangled
    argument.

.EXAMPLE
    .\scripts\setup.ps1
    .\scripts\setup.ps1 -Defaults
    .\scripts\setup.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [switch]$Defaults,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$bash = $null
foreach ($candidate in @(
    "$env:ProgramFiles\Git\bin\bash.exe",
    "${env:ProgramFiles(x86)}\Git\bin\bash.exe",
    "$env:LOCALAPPDATA\Programs\Git\bin\bash.exe"
)) {
    if (Test-Path $candidate) { $bash = $candidate; break }
}
if (-not $bash) {
    $bash = (Get-Command bash.exe -ErrorAction SilentlyContinue).Source
}
if (-not $bash) {
    Write-Error "Git Bash not found. Install Git for Windows, or run scripts/setup.sh from WSL."
    exit 1
}

$env:MSYS_NO_PATHCONV = '1'
$env:MSYS2_ARG_CONV_EXCL = '*'

$argv = @()
if ($Defaults) { $argv += '--defaults' }
if ($DryRun)   { $argv += '--dry-run' }

& $bash 'scripts/setup.sh' @argv
exit $LASTEXITCODE
