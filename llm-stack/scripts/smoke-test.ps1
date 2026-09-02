<#
.SYNOPSIS
    End-to-end verification through Traefik: model listing, auth enforcement,
    a completion, a streaming completion, and Prometheus attribution.

.DESCRIPTION
    Uses curl.exe rather than Invoke-RestMethod. Windows PowerShell's .NET TLS
    stack fails against this stack's private CA and restricted cipher list
    ("Could not create SSL/TLS secure channel"); curl.exe handles it with
    --cacert and ships with Windows 10+.

.PARAMETER Direct
    Test vLLM at api.<domain> instead of the gateway. Needs an Authelia token,
    which is fetched automatically.

.EXAMPLE
    .\scripts\smoke-test.ps1
    .\scripts\smoke-test.ps1 -Direct
#>
[CmdletBinding()]
param(
    [switch]$Direct,
    [string]$Prompt = 'In one sentence, what is a KV cache in LLM inference?'
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$curl = (Get-Command curl.exe -ErrorAction SilentlyContinue).Source
if (-not $curl) { Write-Host 'curl.exe not found (Windows 10+ ships it)' -ForegroundColor Red; exit 1 }

$envMap = @{}
foreach ($line in (Get-Content '.env')) {
    if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $envMap[$matches[1]] = $matches[2].Trim() }
}
$dom = $envMap['LLM_DOMAIN']; if (-not $dom) { $dom = 'llm.localhost' }
$ca = Join-Path $root 'config/traefik/certs/ca.crt'

if ($Direct) {
    $base = "https://api.$dom"
    $key = (& "$PSScriptRoot/get-token.ps1")
    $model = $envMap['VLLM_SERVED_MODEL_NAME']; if (-not $model) { $model = 'default' }
} else {
    # The gateway is the default: LiteLLM validates its own keys, so no
    # Authelia session or token is involved.
    $base = "https://gateway.$dom"
    $key = $envMap['LITELLM_MASTER_KEY']
    $model = 'local'
}

# --ssl-no-revoke: the private CA publishes no CRL or OCSP responder, and
# schannel treats "revocation status unknown" as a hard failure.
$common = @('--ssl-no-revoke', '--cacert', $ca, '-s')

# Windows PowerShell mangles quotes when passing an argument to a native exe,
# so JSON given with -d arrives corrupted ("Invalid JSON payload"). Writing it
# to a file and using -d "@file" side-steps the whole quoting problem.
function Invoke-CurlJson {
    param([string[]]$CurlArgs, [string]$Json)
    $tmp = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($tmp, $Json, (New-Object Text.UTF8Encoding($false)))
        & $curl @CurlArgs -d "@$tmp"
    } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

$pass = 0; $fail = 0
function Test-Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "==> $Name" -ForegroundColor Cyan
    try { & $Body; Write-Host '    PASS' -ForegroundColor Green; $script:pass++ }
    catch { Write-Host "    FAIL  $($_.Exception.Message)" -ForegroundColor Red; $script:fail++ }
}

Write-Host ''
Write-Host "  Target: $base  (model: $model)" -ForegroundColor White
Write-Host ''

# --- 1 ----------------------------------------------------------------------
Test-Step 'GET /v1/models' {
    $out = & $curl @common -H "Authorization: Bearer $key" "$base/v1/models"
    $d = $out | ConvertFrom-Json
    if (-not $d.data) { throw "no models returned: $out" }
    foreach ($m in $d.data) { Write-Host "    - $($m.id)" -ForegroundColor DarkGray }
}

# --- 2 ----------------------------------------------------------------------
Test-Step 'Rejects unauthenticated requests' {
    $code = & $curl @common -o NUL -w '%{http_code}' "$base/v1/models"
    if ($code -eq '200') { throw 'endpoint served an unauthenticated request' }
    Write-Host "    HTTP $code as expected" -ForegroundColor DarkGray
}

# --- 3 ----------------------------------------------------------------------
Test-Step 'POST /v1/chat/completions' {
    $body = @{ model = $model; messages = @(@{role='user'; content=$Prompt});
               max_tokens = 120; temperature = 0.2 } | ConvertTo-Json -Depth 6 -Compress
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = Invoke-CurlJson -Json $body -CurlArgs ($common + @(
        '-X','POST','-H',"Authorization: Bearer $key",
        '-H','Content-Type: application/json',"$base/v1/chat/completions"))
    $sw.Stop()
    $d = $out | ConvertFrom-Json
    $text = $d.choices[0].message.content
    if (-not $text) { throw "empty completion: $out" }
    Write-Host "    latency      $($sw.ElapsedMilliseconds) ms" -ForegroundColor DarkGray
    Write-Host "    prompt tok   $($d.usage.prompt_tokens)" -ForegroundColor DarkGray
    Write-Host "    output tok   $($d.usage.completion_tokens)" -ForegroundColor DarkGray
    if ($sw.Elapsed.TotalSeconds -gt 0) {
        $tps = [math]::Round($d.usage.completion_tokens / $sw.Elapsed.TotalSeconds, 1)
        Write-Host "    throughput   $tps tok/s" -ForegroundColor DarkGray
    }
    Write-Host ''
    Write-Host "    $($text.Trim())" -ForegroundColor White
    Write-Host ''
}

# --- 4 ----------------------------------------------------------------------
# Streaming is what the chat UI uses and exercises a different code path from
# the buffered response above, so it is tested separately.
Test-Step 'Streaming (SSE) completion' {
    $body = @{ model = $model; messages = @(@{role='user'; content='Count from 1 to 5.'});
               max_tokens = 40; stream = $true } | ConvertTo-Json -Depth 6 -Compress
    # -N disables curl's own buffering so frames arrive as they are produced.
    $out = Invoke-CurlJson -Json $body -CurlArgs ($common + @(
        '-N','-X','POST','-H',"Authorization: Bearer $key",
        '-H','Content-Type: application/json',"$base/v1/chat/completions"))
    # -notlike '*[DONE]*' is a trap: in a wildcard pattern [DONE] is a CHARACTER
    # CLASS, so it matches any one of D,O,N,E - which excludes nearly every
    # frame. Match the literal with -notmatch and an escaped bracket instead.
    $lines = @($out)
    $chunks = @($lines | Where-Object { $_ -like 'data: *' -and $_ -notmatch '\[DONE\]' }).Count
    if ($chunks -lt 2) {
        throw "expected multiple SSE chunks, got $chunks (of $($lines.Count) lines; first: $($lines[0]))"
    }
    Write-Host "    chunks       $chunks" -ForegroundColor DarkGray
}

# --- 5 ----------------------------------------------------------------------
Test-Step 'Prometheus recorded the requests' {
    # Prometheus has no published port any more; query it from inside llm-net.
    $out = docker run --rm --network llm-net curlimages/curl:8.11.1 -s -m 15 `
        --get --data-urlencode 'query=sum(vllm:request_success_total)' `
        'http://prometheus:9090/api/v1/query' 2>$null
    $d = $out | ConvertFrom-Json
    if ($d.data.result.Count -eq 0) { throw 'no vllm:request_success_total series yet - retry in ~15s' }
    Write-Host "    total successful requests: $($d.data.result[0].value[1])" -ForegroundColor DarkGray
}

Write-Host ''
Write-Host "  $pass passed, $fail failed" -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
Write-Host ''
exit $fail
