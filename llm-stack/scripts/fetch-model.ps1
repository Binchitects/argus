<#
.SYNOPSIS
    Download a HuggingFace model into ./models so vLLM can serve it from disk.

.DESCRIPTION
    Uses the official `hf` CLI inside a throwaway container, so nothing needs to
    be installed on the host.

    Why not `git clone`? A repo clone pulls EVERY weight format the authors
    published — .safetensors AND .bin, often plus GGUF and ONNX copies. For a 7B
    model that is frequently 40+ GB to get the 15 GB you actually need. It also
    restarts from zero if the connection drops.

    `hf download` fetches per file, skips files already complete, and resumes
    partial ones — which matters a lot on an unreliable link.

.PARAMETER Repo
    HuggingFace repo id, e.g. Qwen/Qwen2.5-7B-Instruct

.PARAMETER Name
    Folder name under models\. Defaults to the part after the slash.

.PARAMETER IncludeBin
    Also download .bin weights. Only needed for models with no safetensors.

.PARAMETER Workers
    Parallel file downloads. Lower this (1-2) on a flaky connection.

.EXAMPLE
    .\scripts\fetch-model.ps1 -Repo "Qwen/Qwen2.5-7B-Instruct"
    .\scripts\fetch-model.ps1 -Repo "Qwen/Qwen2.5-14B-Instruct-AWQ" -Workers 2
    .\scripts\fetch-model.ps1 -Repo "meta-llama/Llama-3.1-8B-Instruct" -Token hf_xxx
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repo,
    [string]$Name,
    [string]$Token,
    [switch]$IncludeBin,
    [int]$Workers = 4
)

# Native CLIs write progress to stderr; under 'Stop' that would abort the script.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Name) { $Name = ($Repo -split '/')[-1] }
$target = Join-Path $root "models\$Name"

if (-not $Token) {
    $envFile = Join-Path $root '.env'
    if (Test-Path $envFile) {
        foreach ($line in (Get-Content $envFile)) {
            if ($line -match '^\s*HF_TOKEN=(.*)$') { $Token = $matches[1].Trim() }
        }
    }
}

New-Item -ItemType Directory -Path $target -Force | Out-Null

# Only the files vLLM actually loads. Excluding the duplicate formats is where
# most of the bandwidth saving comes from.
$include = @('*.safetensors', '*.json', '*.txt', '*.model', '*.py')
if ($IncludeBin) { $include += '*.bin' }

$exclude = @(
    'original/*',        # Meta ships a second full copy of Llama weights here
    'onnx/*', 'openvino/*', 'coreml/*',
    '*.gguf', '*.pth', '*.msgpack', '*.h5'
)
if (-not $IncludeBin) { $exclude += '*.bin' }

Write-Host ''
Write-Host "  repo    $Repo"      -ForegroundColor White
Write-Host "  target  models\$Name" -ForegroundColor White
Write-Host "  include $($include -join ' ')" -ForegroundColor DarkGray
Write-Host "  exclude $($exclude -join ' ')" -ForegroundColor DarkGray
Write-Host ''
Write-Host '  Re-run this command if it drops; completed files are skipped and' -ForegroundColor DarkGray
Write-Host '  partial ones resume.' -ForegroundColor DarkGray
Write-Host ''

$incArgs = ($include | ForEach-Object { "--include `"$_`"" }) -join ' '
$excArgs = ($exclude | ForEach-Object { "--exclude `"$_`"" }) -join ' '
$loginCmd = ''
if ($Token) { $loginCmd = "export HF_TOKEN='$Token';" }

# HF_HUB_ENABLE_HF_TRANSFER is deliberately NOT set: it is faster on a healthy
# link but far less forgiving of resets, which is the opposite of what we want.
$script = @"
set -e
pip install --quiet --no-cache-dir 'huggingface_hub[cli]' 2>/dev/null
$loginCmd
hf download '$Repo' --local-dir /out --max-workers $Workers $incArgs $excArgs
"@

docker run --rm `
    -v "${target}:/out" `
    -e HF_HUB_DISABLE_TELEMETRY=1 `
    python:3.12-slim `
    bash -lc $script

if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '  Download did not complete. Re-run the same command to resume.' -ForegroundColor Yellow
    exit 1
}

# vLLM needs config.json at the root of the directory it is pointed at.
if (-not (Test-Path (Join-Path $target 'config.json'))) {
    Write-Host ''
    Write-Host '  WARNING: no config.json in the target directory.' -ForegroundColor Yellow
    Write-Host '  vLLM will not be able to load this path.' -ForegroundColor Yellow
    exit 1
}

$size = (Get-ChildItem $target -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1GB
$weights = (Get-ChildItem $target -Recurse -Filter '*.safetensors' | Measure-Object).Count

Write-Host ''
Write-Host "  Done. $([math]::Round($size,2)) GB, $weights safetensors shard(s)." -ForegroundColor Green
Write-Host ''
Write-Host '  Serve it with:' -ForegroundColor White
Write-Host "      .\scripts\switch-model.ps1 -Model `"/models/$Name`"" -ForegroundColor Cyan
Write-Host ''
