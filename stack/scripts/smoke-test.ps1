# End to end through the running stack: sign in as the administrator, ask the
# model a question in a new conversation, and read the streamed answer back.
. "$PSScriptRoot\lib.ps1"
$base = "http://127.0.0.1:$(EnvGet 'ARGUS_HTTP_PORT' '8080')"
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$headers = @{ 'X-Argus-Request' = '1' }
Write-Host "smoke test against $base"
try {
    $login = @{ username = (EnvGet 'ARGUS_ADMIN_USERNAME' 'admin'); password = (EnvGet 'ARGUS_ADMIN_PASSWORD') } | ConvertTo-Json
    Invoke-RestMethod "$base/api/auth/login" -Method Post -Body $login -ContentType 'application/json' -WebSession $session | Out-Null
    Ok 'signed in'
} catch { Bad "sign-in failed: $($_.Exception.Message)"; exit 1 }
try {
    $models = Invoke-RestMethod "$base/api/models" -WebSession $session
    if ($models.Count -gt 0) { Ok "models: $($models -join ', ')" } else { Bad 'the gateway lists no model' }
} catch { Bad "models: $($_.Exception.Message)" }
$conv = Invoke-RestMethod "$base/api/conversations" -Method Post -Body '{}' -ContentType 'application/json' -Headers $headers -WebSession $session
Ok "conversation $($conv.id)"
$started = Get-Date
$body = '{"content":"Reply with the single word: ready","tools":false}'
$stream = (Invoke-WebRequest "$base/api/conversations/$($conv.id)/messages" -Method Post -Body $body -ContentType 'application/json' `
    -Headers $headers -WebSession $session -UseBasicParsing -TimeoutSec 600).Content
if ($stream -match '"type":"done"') {
    $answer = ($stream -split "`n" | Where-Object { $_ -like 'data: {"type":"content"*' } | ForEach-Object { ($_.Substring(6) | ConvertFrom-Json).text }) -join ''
    Ok ("the model answered in {0:N0}s: {1}" -f ((Get-Date) - $started).TotalSeconds, $answer)
} else { Bad "no answer: $stream" }
Invoke-RestMethod "$base/api/conversations/$($conv.id)" -Method Delete -Headers $headers -WebSession $session | Out-Null
if ($script:Failed -eq 0) { Write-Host "ready to use: $base" } else { Write-Host "$($script:Failed) problem(s)" }
exit $script:Failed
