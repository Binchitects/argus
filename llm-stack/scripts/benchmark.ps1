<#
.SYNOPSIS
    Run a concurrency sweep against the local vLLM endpoint.

.DESCRIPTION
    Executes scripts/benchmark.py inside a throwaway python:3.12-slim container
    attached to llm-net. Running it in-network rather than from the host removes
    the published-port hop and any host Python dependency.

.EXAMPLE
    .\scripts\benchmark.ps1
    .\scripts\benchmark.ps1 -Concurrency "1,2,4,8,16,32" -Requests 32 -MaxTokens 512
#>
[CmdletBinding()]
param(
    [string]$Concurrency = '1,4,8,16',
    [int]$Requests = 16,
    [int]$MaxTokens = 256,
    [switch]$UseGateway
)

# Native CLIs write progress and warnings to stderr. Under 'Stop', PowerShell
# turns any native stderr line into a terminating NativeCommandError, so
# docker's ordinary output would abort the script. Check $LASTEXITCODE instead.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$envMap = @{}
foreach ($line in (Get-Content '.env')) {
    if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $envMap[$matches[1]] = $matches[2].Trim() }
}

if ($UseGateway) {
    $baseUrl = 'http://litellm:4000'
    $apiKey = $envMap['LITELLM_MASTER_KEY']
    $model = 'local'
} else {
    # Which engine is actually enabled decides the endpoint. Hardcoding vllm
    # here meant a llama.cpp deploy benchmarked a container that does not exist
    # and failed with a DNS error -- the same assumption up.sh already dropped.
    if ($envMap['COMPOSE_PROFILES'] -split ',' -contains 'llamacpp') {
        $baseUrl = 'http://llamacpp:8080'
        $apiKey = $envMap['LLAMACPP_API_KEY']
        $model = $envMap['LLAMACPP_SERVED_MODEL_NAME']
    } else {
        $baseUrl = 'http://vllm:8000'
        $apiKey = $envMap['VLLM_API_KEY']
        $model = $envMap['VLLM_SERVED_MODEL_NAME']
    }
    if (-not $model) { $model = 'default' }
}

Write-Host ''
Write-Host '  Running benchmark inside a container on llm-net...' -ForegroundColor Cyan
Write-Host '  Watch it live on the LLM Overview dashboard.' -ForegroundColor DarkGray

docker run --rm `
    --network llm-net `
    -v "${PSScriptRoot}:/work:ro" `
    python:3.12-slim `
    python /work/benchmark.py `
        --base-url $baseUrl `
        --api-key $apiKey `
        --model $model `
        --concurrency $Concurrency `
        --requests $Requests `
        --max-tokens $MaxTokens

exit $LASTEXITCODE
