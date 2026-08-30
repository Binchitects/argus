<#
.SYNOPSIS
    Tail logs from one service or the whole stack.

.EXAMPLE
    .\scripts\logs.ps1                 # everything, following
    .\scripts\logs.ps1 vllm            # just the inference engine
    .\scripts\logs.ps1 vllm -Tail 200
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Service,
    [int]$Tail = 100,
    [switch]$NoFollow
)

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$dockerArgs = @('compose', 'logs', '--tail', "$Tail")
if (-not $NoFollow) { $dockerArgs += '--follow' }
if ($Service) { $dockerArgs += $Service }

& docker @dockerArgs
