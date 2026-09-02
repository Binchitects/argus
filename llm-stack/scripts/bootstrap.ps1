<#
.SYNOPSIS
    One-time setup for the LLMService stack.

.DESCRIPTION
    Verifies Docker and GPU access, creates .env from the template, and fills
    every placeholder secret with a cryptographically random value. Safe to
    re-run: existing .env values are preserved unless -Force is passed.

.EXAMPLE
    .\scripts\bootstrap.ps1
    .\scripts\bootstrap.ps1 -Force      # regenerate all secrets
#>
[CmdletBinding()]
param(
    [switch]$Force
)

# Native CLIs write progress and warnings to stderr. Under 'Stop', PowerShell
# turns any native stderr line into a terminating NativeCommandError, so
# docker's ordinary output would abort the script. Check $LASTEXITCODE instead.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
$envTemplate = Join-Path $root '.env.example'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    !   $msg" -ForegroundColor Yellow }
function Write-Fail($msg) { Write-Host "    X   $msg" -ForegroundColor Red }

# Cryptographically secure random hex. Used for every generated secret so that
# nothing in .env is predictable from the machine state.
function New-Secret {
    param([int]$ByteCount = 24)
    $bytes = New-Object byte[] $ByteCount
    $rng = [System.Security.Cryptography.RNGCryptoServiceProvider]::new()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return (([System.BitConverter]::ToString($bytes)) -replace '-', '').ToLower()
}

Write-Host ''
Write-Host '  LLMService bootstrap' -ForegroundColor White
Write-Host '  --------------------' -ForegroundColor DarkGray
Write-Host ''

# ---------------------------------------------------------------------------
# 1. Prerequisites
# ---------------------------------------------------------------------------
Write-Step 'Checking prerequisites'

$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker) {
    Write-Fail 'docker not found on PATH. Install Docker Desktop and re-run.'
    exit 1
}
Write-Ok (docker --version)

$null = docker compose version 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Fail 'The docker compose plugin is unavailable.'
    exit 1
}
Write-Ok (docker compose version)

$null = docker info 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Fail 'The Docker daemon is not responding. Start Docker Desktop and re-run.'
    exit 1
}
Write-Ok 'Docker daemon reachable'

# ---------------------------------------------------------------------------
# 2. GPU access
# ---------------------------------------------------------------------------
Write-Step 'Checking GPU'

$smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($smi) {
    $gpuInfo = nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader
    foreach ($line in $gpuInfo) { Write-Ok $line }
} else {
    Write-Warn2 'nvidia-smi not found on the host. vLLM requires an NVIDIA GPU.'
}

# The real test is whether a *container* can see the GPU — a working host
# driver does not imply the container toolkit is wired up.
Write-Step 'Verifying GPU passthrough into containers (may pull a small image)'
$gpuTest = docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi -L 2>&1
if ($LASTEXITCODE -eq 0) {
    # The captured stream also carries docker's image-pull progress; keep only
    # the actual `nvidia-smi -L` device lines.
    foreach ($line in ($gpuTest | Where-Object { $_ -match '^GPU \d+:' })) { Write-Ok $line }
} else {
    Write-Warn2 'Containers cannot reach the GPU. vLLM will fail to start.'
    Write-Warn2 'On Windows: enable WSL2 integration in Docker Desktop settings.'
    Write-Warn2 'On Linux: install nvidia-container-toolkit and restart dockerd.'
}

# ---------------------------------------------------------------------------
# 3. .env
# ---------------------------------------------------------------------------
Write-Step 'Preparing .env'

if (-not (Test-Path $envTemplate)) {
    Write-Fail ".env.example is missing at $envTemplate"
    exit 1
}

if (-not (Test-Path $envFile)) {
    Copy-Item $envTemplate $envFile
    Write-Ok 'Created .env from .env.example'
} else {
    Write-Ok '.env already exists (values will be preserved)'
}

$content = Get-Content $envFile -Raw

# key -> generator. Only entries still holding a placeholder get replaced,
# so re-running never invalidates a working stack.
$secrets = @{
    'VLLM_API_KEY'            = { 'sk-local-' + (New-Secret 24) }
    'GRAFANA_ADMIN_PASSWORD'  = { New-Secret 12 }
    'WEBUI_SECRET_KEY'        = { New-Secret 32 }
    'POSTGRES_PASSWORD'       = { New-Secret 16 }
    'LITELLM_MASTER_KEY'      = { 'sk-' + (New-Secret 20) }
    'LITELLM_SALT_KEY'        = { New-Secret 16 }
    'CLICKHOUSE_PASSWORD'     = { New-Secret 16 }
    'MINIO_ROOT_PASSWORD'     = { New-Secret 16 }
    'LANGFUSE_SALT'           = { New-Secret 16 }
    'LANGFUSE_NEXTAUTH_SECRET' = { New-Secret 32 }
    # Langfuse requires exactly 32 bytes expressed as 64 hex characters.
    'LANGFUSE_ENCRYPTION_KEY' = { New-Secret 32 }
    # Basic-auth password Traefik enforces on the services that have no login.
    'PROXY_AUTH_PASSWORD'     = { New-Secret 12 }
}

$changed = 0
foreach ($key in $secrets.Keys) {
    $pattern = "(?m)^$key=(.*)$"
    $match = [regex]::Match($content, $pattern)
    if (-not $match.Success) {
        Write-Warn2 "$key not present in .env; skipping"
        continue
    }

    $current = $match.Groups[1].Value
    $isPlaceholder = ($current -match 'change-me') -or
                     ($current -eq '') -or
                     ($current -match '^0{16,}$')

    if ($isPlaceholder -or $Force) {
        $value = & $secrets[$key]
        $content = [regex]::Replace($content, $pattern, "$key=$value")
        $changed++
    }
}

Set-Content -Path $envFile -Value $content -Encoding UTF8 -NoNewline
if ($changed -gt 0) {
    Write-Ok "Generated $changed secret(s)"
} else {
    Write-Ok 'All secrets already set'
}

# ---------------------------------------------------------------------------
# 4. Directories
# ---------------------------------------------------------------------------
Write-Step 'Creating directories'
$modelsDir = Join-Path $root 'models'
if (-not (Test-Path $modelsDir)) {
    New-Item -ItemType Directory -Path $modelsDir | Out-Null
}
Write-Ok "models/  (drop local weights here to serve without HuggingFace)"

# ---------------------------------------------------------------------------
# 4b. TLS certificates and basic-auth for the reverse proxy
# ---------------------------------------------------------------------------
$profileMap = @{}
foreach ($line in (Get-Content $envFile)) {
    if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $profileMap[$matches[1]] = $matches[2].Trim() }
}
if ($profileMap['COMPOSE_PROFILES'] -match 'proxy') {
    Write-Step 'Generating TLS certificates and basic-auth for Traefik'
    $genArgs = @()
    if ($Force) { $genArgs += '-Force' }
    & (Join-Path $PSScriptRoot 'gen-certs.ps1') @genArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Warn2 'Certificate generation failed; the proxy profile will not start.'
    }
} else {
    Write-Ok 'proxy profile not enabled; skipping certificate generation'
}

# ---------------------------------------------------------------------------
# 5. Validate the compose file resolves
# ---------------------------------------------------------------------------
Write-Step 'Validating docker-compose.yml'
Push-Location $root
try {
    $null = docker compose config --quiet 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Ok 'Compose file is valid'
    } else {
        Write-Fail 'Compose validation failed (see output above)'
        exit 1
    }
} finally {
    Pop-Location
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
$envMap = @{}
foreach ($line in (Get-Content $envFile)) {
    if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $envMap[$matches[1]] = $matches[2] }
}

Write-Host ''
Write-Host '  Ready.' -ForegroundColor Green
Write-Host ''
Write-Host '  Model      : ' -NoNewline -ForegroundColor DarkGray; Write-Host $envMap['VLLM_MODEL']
Write-Host '  Profiles   : ' -NoNewline -ForegroundColor DarkGray; Write-Host $envMap['COMPOSE_PROFILES']
Write-Host '  API key    : ' -NoNewline -ForegroundColor DarkGray; Write-Host $envMap['VLLM_API_KEY']
Write-Host '  Grafana    : ' -NoNewline -ForegroundColor DarkGray; Write-Host ("{0} / {1}" -f $envMap['GRAFANA_ADMIN_USER'], $envMap['GRAFANA_ADMIN_PASSWORD'])
Write-Host ''
Write-Host '  Next:  .\scripts\up.ps1' -ForegroundColor White
Write-Host ''
Write-Host '  The first start downloads the model weights (several GB) and can' -ForegroundColor DarkGray
Write-Host '  take 10+ minutes before the API answers. Follow along with:' -ForegroundColor DarkGray
Write-Host '      docker logs -f vllm' -ForegroundColor DarkGray
Write-Host ''
