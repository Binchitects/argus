<#
.SYNOPSIS
    Add the stack's hostnames to the Windows hosts file.

.DESCRIPTION
    Browsers resolve any *.localhost name to 127.0.0.1 by specification, but
    curl, PowerShell and most SDKs do NOT. With the proxy-only setup there are
    no direct ports left to fall back on, so command-line tools need real
    entries.

    Requires administrator rights to write the hosts file; re-launches through
    UAC if needed.

.PARAMETER Remove
    Take the entries out again.

.EXAMPLE
    .\scripts\setup-hosts.ps1
    .\scripts\setup-hosts.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch]$Remove,
    [switch]$NoElevate
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated) -and -not $NoElevate) {
    Write-Host 'Writing the hosts file needs administrator rights - re-launching.' -ForegroundColor Yellow
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-NoElevate')
    if ($Remove) { $argList += '-Remove' }
    try {
        $p = Start-Process powershell.exe -ArgumentList $argList -Verb RunAs -Wait -PassThru
        exit $p.ExitCode
    } catch {
        Write-Host 'UAC declined.' -ForegroundColor Red
        exit 1
    }
}

# Read LLM_DOMAIN and the subdomains actually in use.
$domain = 'llm.localhost'
$envFile = Join-Path $root '.env'
if (Test-Path $envFile) {
    foreach ($line in (Get-Content $envFile)) {
        if ($line -match '^\s*LLM_DOMAIN=(.*)$') { $domain = $matches[1].Trim() }
    }
}

$names = @('', 'auth', 'chat', 'api', 'grafana', 'metrics', 'alerts', 'gateway',
           'traces', 'logs', 'cadvisor', 'node', 'gpu', 's3', 'argus') |
    ForEach-Object { if ($_) { "$_.$domain" } else { $domain } }

$hostsPath = "$env:SystemRoot\System32\drivers\etc\hosts"
$marker = '# --- LLMService ---'
$endMarker = '# --- end LLMService ---'

$content = Get-Content $hostsPath -Raw -ErrorAction Stop
# Always strip the old block first so this is idempotent.
$pattern = [regex]::Escape($marker) + '.*?' + [regex]::Escape($endMarker) + '\r?\n?'
$content = [regex]::Replace($content, $pattern, '', 'Singleline')

if (-not $Remove) {
    $block = $marker + "`r`n"
    foreach ($n in $names) { $block += "127.0.0.1`t$n`r`n" }
    $block += $endMarker + "`r`n"
    $content = $content.TrimEnd() + "`r`n`r`n" + $block
}

Set-Content -Path $hostsPath -Value $content -Encoding ASCII -Force

if ($Remove) {
    Write-Host "  Removed LLMService entries from $hostsPath" -ForegroundColor Green
} else {
    Write-Host "  Added $($names.Count) entries to $hostsPath" -ForegroundColor Green
    $names | ForEach-Object { Write-Host "    127.0.0.1  $_" -ForegroundColor DarkGray }
    Write-Host ''
    Write-Host '  curl and PowerShell can now reach the stack by hostname.' -ForegroundColor DarkGray
}

# DNS cache holds negative lookups too.
ipconfig /flushdns | Out-Null
Write-Host '  DNS cache flushed' -ForegroundColor DarkGray
