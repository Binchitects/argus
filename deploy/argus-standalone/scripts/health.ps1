# Is every part of the stack up and answering? Exit code = number of failures.
. "$PSScriptRoot\lib.ps1"
$port = EnvGet 'ARGUS_HTTP_PORT' '8080'
$gw = EnvGet 'GATEWAY_PORT' '4000'
Write-Host 'containers'
foreach ($svc in 'llamacpp', 'llamacpp-embed', 'postgres', 'litellm', 'argus') {
    $state = docker inspect -f '{{.State.Status}}{{if .State.Health}}/{{.State.Health.Status}}{{end}}' $svc 2>$null
    if (-not $state) { $state = 'missing' }
    if ($state -in 'running', 'running/healthy') { Ok "$svc ($state)" } else { Bad "$svc ($state)" }
}
Write-Host 'endpoints'
function Probe([string]$Url) { try { (Invoke-WebRequest -Uri $Url -TimeoutSec 5 -UseBasicParsing).StatusCode -eq 200 } catch { $false } }
if (Probe "http://127.0.0.1:$port/healthz") { Ok "app        http://127.0.0.1:$port" } else { Bad "app does not answer on :$port" }
if (Probe "http://127.0.0.1:$gw/health/liveliness") { Ok "gateway    http://127.0.0.1:$gw/v1" } else { Bad "gateway does not answer on :$gw" }
docker exec llamacpp curl -fsS -m 5 http://localhost:8080/health *> $null
if ($LASTEXITCODE -eq 0) { Ok "chat model $(EnvGet 'MODEL_NAME')" } else { Bad 'chat model not ready (a large model takes minutes to load: docker logs -f llamacpp)' }
docker exec llamacpp-embed curl -fsS -m 5 http://localhost:8080/health *> $null
if ($LASTEXITCODE -eq 0) { Ok 'embeddings' } else { Bad 'embedding server not ready' }
if ($script:Failed -eq 0) { Write-Host "all healthy: open http://localhost:$port" } else { Write-Host "$($script:Failed) problem(s)" }
exit $script:Failed
