<#
.SYNOPSIS
    Change the served model and restart only the inference container.

.DESCRIPTION
    Rewrites VLLM_MODEL (and optionally the memory/context knobs) in .env, then
    recreates just the vllm service. Everything else — dashboards, metrics
    history, chat history — stays up.

    Sizing guide for a 24 GB card, weights only (KV cache needs the rest):
        7-8B   fp16   ~15 GB    comfortable
        13-14B fp16   ~28 GB    too big -> use AWQ/GPTQ 4-bit (~8 GB)
        32B    AWQ    ~19 GB    tight; lower --max-model-len
        70B    AWQ    ~38 GB    needs two cards

.EXAMPLE
    .\scripts\switch-model.ps1 -Model "Qwen/Qwen2.5-14B-Instruct-AWQ" -ExtraArgs "--quantization awq"
    .\scripts\switch-model.ps1 -Model "mistralai/Mistral-7B-Instruct-v0.3" -MaxModelLen 16384
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Model,
    [string]$ServedName,
    [int]$MaxModelLen,
    [double]$GpuMemoryUtilization,
    [string]$ExtraArgs,
    [switch]$NoRestart
)

# Native CLIs write progress and warnings to stderr. Under 'Stop', PowerShell
# turns any native stderr line into a terminating NativeCommandError, so
# docker's ordinary output would abort the script. Check $LASTEXITCODE instead.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$envFile = Join-Path $root '.env'

function Set-EnvValue([string]$content, [string]$key, [string]$value) {
    $pattern = "(?m)^$key=.*$"
    if ([regex]::IsMatch($content, $pattern)) {
        return [regex]::Replace($content, $pattern, "$key=$value")
    }
    return $content.TrimEnd() + "`n$key=$value`n"
}

$content = Get-Content $envFile -Raw
$content = Set-EnvValue $content 'VLLM_MODEL' $Model
Write-Host "VLLM_MODEL                   -> $Model" -ForegroundColor Cyan

if ($ServedName) {
    $content = Set-EnvValue $content 'VLLM_SERVED_MODEL_NAME' $ServedName
    Write-Host "VLLM_SERVED_MODEL_NAME       -> $ServedName" -ForegroundColor Cyan
}
if ($PSBoundParameters.ContainsKey('MaxModelLen')) {
    $content = Set-EnvValue $content 'VLLM_MAX_MODEL_LEN' $MaxModelLen
    Write-Host "VLLM_MAX_MODEL_LEN           -> $MaxModelLen" -ForegroundColor Cyan
}
if ($PSBoundParameters.ContainsKey('GpuMemoryUtilization')) {
    $content = Set-EnvValue $content 'VLLM_GPU_MEMORY_UTILIZATION' $GpuMemoryUtilization
    Write-Host "VLLM_GPU_MEMORY_UTILIZATION  -> $GpuMemoryUtilization" -ForegroundColor Cyan
}
if ($PSBoundParameters.ContainsKey('ExtraArgs')) {
    $content = Set-EnvValue $content 'VLLM_EXTRA_ARGS' $ExtraArgs
    Write-Host "VLLM_EXTRA_ARGS              -> $ExtraArgs" -ForegroundColor Cyan
}

Set-Content -Path $envFile -Value $content -Encoding UTF8 -NoNewline

# ---------------------------------------------------------------------------
# The served name is what clients, the Open WebUI picker and every Grafana
# `model_name` label show. Derive it from the checkpoint, then keep the gateway
# in step - LiteLLM does not expand ${VAR} in its YAML, so it must be literal.
# ---------------------------------------------------------------------------
if (-not $ServedName) {
    $ServedName = ($Model.TrimEnd('/') -split '/')[-1]
    $content = Set-EnvValue $content 'VLLM_SERVED_MODEL_NAME' $ServedName
    Set-Content -Path $envFile -Value $content -Encoding UTF8 -NoNewline
    Write-Host "VLLM_SERVED_MODEL_NAME       -> $ServedName" -ForegroundColor Cyan
}

$litellmCfg = Join-Path $root 'config/litellm/config.yaml'
if (Test-Path $litellmCfg) {
    $cfg = Get-Content $litellmCfg -Raw -Encoding UTF8
    # First model_name entry is the real-name one; the `local` alias keeps its name.
    # NOTE: [regex]::Replace(input, pattern, replacement, N) takes RegexOptions
    # as the 4th argument, NOT a count - passing 1 means IgnoreCase and replaces
    # every match, which would rename the `local` alias too. An instance Regex
    # is the only overload that accepts a real count.
    $rx = New-Object System.Text.RegularExpressions.Regex('(?m)^  - model_name: .*$')
    $cfg = $rx.Replace($cfg, "  - model_name: $ServedName", 1)
    $cfg = [regex]::Replace($cfg, '(?m)^      model: openai/.*$', "      model: openai/$ServedName")
    [IO.File]::WriteAllText($litellmCfg, $cfg, (New-Object Text.UTF8Encoding($false)))
    Write-Host "gateway config               -> $ServedName" -ForegroundColor Cyan
}

if ($NoRestart) {
    Write-Host ''
    Write-Host '.env updated. Apply with:' -ForegroundColor Yellow
    Write-Host '    docker compose up -d --force-recreate vllm litellm' -ForegroundColor Yellow
    exit 0
}

Write-Host ''
Write-Host '==> Recreating the vllm container only' -ForegroundColor Cyan
# The gateway caches its model list at startup, so recreate it too.
$targets = @('vllm')
if ((Get-Content $envFile | Where-Object { $_ -match '^COMPOSE_PROFILES=.*gateway' })) {
    $targets += 'litellm'
}
docker compose up -d --force-recreate @targets
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host ''
Write-Host 'Weights for a new model download on first use. Follow progress with:' -ForegroundColor DarkGray
Write-Host '    docker logs -f vllm' -ForegroundColor DarkGray
Write-Host ''
Write-Host 'Then verify:  .\scripts\smoke-test.ps1' -ForegroundColor DarkGray
