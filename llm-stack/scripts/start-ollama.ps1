# Start Ollama with the settings this stack's context window depends on.
# See start-ollama.sh for the measured reasoning; the short version is that
# OLLAMA_KV_CACHE_TYPE is server-level environment, so starting Ollama any
# other way silently drops the usable window from 131,072 to 65,536 and
# spills the remainder to system RAM instead of failing.
[CmdletBinding()]
param(
    [string]$KvCacheType = 'q4_0',
    [int]$ContextLength  = 131072
)
$ErrorActionPreference = 'Stop'

$running = docker compose ps --status running --services 2>$null
if ($running -contains 'vllm') {
    Write-Error "vllm is running and holds the GPU. Stop it first: docker compose stop vllm"
}

# A forced stop of ollama.exe does NOT reap its llama-server.exe children, and
# each orphan keeps a full copy of the model resident. Measured here: three
# orphans pinned the card at 24.1 GB of 24.6 GB and made every later
# measurement wrong. Reap them explicitly.
Get-Process llama-server, ollama -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 4

$env:OLLAMA_FLASH_ATTENTION = '1'
$env:OLLAMA_KV_CACHE_TYPE   = $KvCacheType
$env:OLLAMA_CONTEXT_LENGTH  = "$ContextLength"

Write-Host "flash_attention=1 kv_cache=$KvCacheType context=$ContextLength"
Start-Process -FilePath (Get-Command ollama).Source -ArgumentList 'serve' -WindowStyle Hidden
