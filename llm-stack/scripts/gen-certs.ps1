<#
.SYNOPSIS
    Generate the local CA, wildcard TLS certificate, and basic-auth file that
    Traefik needs.

.DESCRIPTION
    Why a private CA rather than a bare self-signed certificate? A self-signed
    leaf must be trusted per hostname; a CA is trusted once and every
    *.llm.localhost name then validates cleanly, including ones you add later.

    Uses the openssl binary. If it is not on PATH, the copy shipped with Git for
    Windows is used automatically.

.EXAMPLE
    .\scripts\gen-certs.ps1
    .\scripts\gen-certs.ps1 -Force      # regenerate everything
    .\scripts\gen-certs.ps1 -Trust      # also install the CA into the Windows trust store
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$Trust
)

# Native CLIs write progress and warnings to stderr. Under 'Stop', PowerShell
# turns any native stderr line into a terminating NativeCommandError, so
# openssl's ordinary output would abort the script. Check $LASTEXITCODE instead.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$certDir = Join-Path $root 'config\traefik\certs'
$authDir = Join-Path $root 'config\traefik\auth'

# --- locate openssl --------------------------------------------------------
$openssl = (Get-Command openssl -ErrorAction SilentlyContinue).Source
if (-not $openssl) {
    $candidates = @(
        "$env:ProgramFiles\Git\usr\bin\openssl.exe",
        "${env:ProgramFiles(x86)}\Git\usr\bin\openssl.exe",
        "$env:LOCALAPPDATA\Programs\Git\usr\bin\openssl.exe"
    )
    $openssl = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $openssl) {
    Write-Host 'openssl not found. Install Git for Windows, or add openssl to PATH.' -ForegroundColor Red
    exit 1
}

# --- read config -----------------------------------------------------------
$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) {
    Write-Host 'No .env found. Run .\scripts\bootstrap.ps1 first.' -ForegroundColor Red
    exit 1
}
$envMap = @{}
foreach ($line in (Get-Content $envFile)) {
    if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $envMap[$matches[1]] = $matches[2].Trim() }
}
$domain = $envMap['LLM_DOMAIN']
if (-not $domain) { $domain = 'llm.localhost' }
$authUser = $envMap['PROXY_AUTH_USER']
if (-not $authUser) { $authUser = 'admin' }
$authPass = $envMap['PROXY_AUTH_PASSWORD']

New-Item -ItemType Directory -Path $certDir -Force | Out-Null
New-Item -ItemType Directory -Path $authDir -Force | Out-Null

$caCrt = Join-Path $certDir 'ca.crt'
$caKey = Join-Path $certDir 'ca.key'
$leafCrt = Join-Path $certDir "$domain.crt"
$leafKey = Join-Path $certDir "$domain.key"
$fullChain = Join-Path $certDir "$domain.fullchain.crt"

# --- CA --------------------------------------------------------------------
if ($Force -or -not (Test-Path $caCrt)) {
    Write-Host '==> Generating local CA' -ForegroundColor Cyan
    & $openssl req -x509 -newkey rsa:4096 -sha256 -days 3650 -nodes `
        -keyout $caKey -out $caCrt `
        -subj '/CN=LLMService Local CA/O=LLMService' `
        -addext 'basicConstraints=critical,CA:TRUE,pathlen:0' `
        -addext 'keyUsage=critical,keyCertSign,cRLSign' 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host '    CA generation failed' -ForegroundColor Red; exit 1 }
    Write-Host '    ca.crt' -ForegroundColor Green
} else {
    Write-Host '==> CA already exists (use -Force to regenerate)' -ForegroundColor DarkGray
}

# --- leaf ------------------------------------------------------------------
if ($Force -or -not (Test-Path $fullChain)) {
    Write-Host "==> Generating wildcard certificate for $domain" -ForegroundColor Cyan

    # A wildcard matches exactly one label, so *.llm.localhost does NOT cover
    # llm.localhost itself. Both names must be listed.
    $extFile = Join-Path $certDir 'leaf.ext'
    @(
        'basicConstraints=CA:FALSE'
        'keyUsage=critical,digitalSignature,keyEncipherment'
        'extendedKeyUsage=serverAuth'
        "subjectAltName=DNS:$domain,DNS:*.$domain,DNS:localhost,IP:127.0.0.1"
    ) | Set-Content -Path $extFile -Encoding ASCII

    $csr = Join-Path $certDir 'leaf.csr'
    & $openssl req -newkey rsa:2048 -nodes -keyout $leafKey -out $csr `
        -subj "/CN=$domain/O=LLMService" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host '    CSR generation failed' -ForegroundColor Red; exit 1 }

    # 825 days is the maximum lifetime browsers accept for a server certificate.
    & $openssl x509 -req -in $csr -CA $caCrt -CAkey $caKey -CAcreateserial `
        -out $leafCrt -days 825 -sha256 -extfile $extFile 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host '    Signing failed' -ForegroundColor Red; exit 1 }

    # Traefik serves the chain so clients that trust the CA validate without
    # needing it presented separately. ASCII avoids a BOM, which would make the
    # PEM unparseable.
    $chain = (Get-Content $leafCrt -Raw) + (Get-Content $caCrt -Raw)
    Set-Content -Path $fullChain -Value $chain -Encoding ASCII -NoNewline

    Remove-Item $csr, $extFile -Force -ErrorAction SilentlyContinue
    Write-Host "    $domain.fullchain.crt" -ForegroundColor Green
    Write-Host "    $domain.key" -ForegroundColor Green
} else {
    Write-Host "==> Certificate for $domain already exists (use -Force to regenerate)" -ForegroundColor DarkGray
}

# --- basic auth ------------------------------------------------------------
$htpasswd = Join-Path $authDir 'users.htpasswd'
if ($Force -or -not (Test-Path $htpasswd)) {
    if (-not $authPass -or $authPass -match 'change-me') {
        Write-Host '!   PROXY_AUTH_PASSWORD is not set in .env; run bootstrap first' -ForegroundColor Red
        exit 1
    }
    Write-Host '==> Generating basic-auth file' -ForegroundColor Cyan
    # apr1 (Apache MD5) is one of the formats Traefik accepts and the only one
    # plain openssl can emit without extra tooling.
    $hash = (& $openssl passwd -apr1 $authPass) | Select-Object -First 1
    Set-Content -Path $htpasswd -Value "${authUser}:${hash}" -Encoding ASCII
    Write-Host "    users.htpasswd  ($authUser)" -ForegroundColor Green
} else {
    Write-Host '==> users.htpasswd already exists (use -Force to regenerate)' -ForegroundColor DarkGray
}

# --- optionally trust the CA ------------------------------------------------
if ($Trust) {
    Write-Host '==> Installing the CA into the current user trust store' -ForegroundColor Cyan
    certutil -addstore -user Root $caCrt | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host '    Installed. Restart the browser for it to take effect.' -ForegroundColor Green
    } else {
        Write-Host '    certutil failed; install manually (see below).' -ForegroundColor Yellow
    }
}

Write-Host ''
if (-not $Trust) {
    Write-Host '  Trust the CA to remove browser warnings:' -ForegroundColor DarkGray
    Write-Host "      .\scripts\gen-certs.ps1 -Trust" -ForegroundColor DarkGray
    Write-Host '  or manually:' -ForegroundColor DarkGray
    Write-Host "      certutil -addstore -user Root `"$caCrt`"" -ForegroundColor DarkGray
    Write-Host ''
}
