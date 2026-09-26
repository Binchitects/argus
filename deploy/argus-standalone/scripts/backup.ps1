# Back up everything that cannot be rebuilt: people, keys and conversations
# (app.db), the code index and installed packs, the gateway's database and
# .env (its LITELLM_SALT_KEY decrypts that database).
. "$PSScriptRoot\lib.ps1"
$dir = EnvGet 'BACKUP_DIR' './backups'
$keep = [int](EnvGet 'BACKUP_KEEP' '14')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $dir "argus-$stamp"
New-Item -ItemType Directory -Force -Path $out | Out-Null
Write-Host "backing up to $out"
docker exec argus argus backup --config /etc/argus/config.yaml --out "/var/lib/argus/backups/$stamp" | Out-Null
if ($LASTEXITCODE -eq 0) {
    docker cp "argus:/var/lib/argus/backups/$stamp" (Join-Path $out 'argus') | Out-Null
    docker exec argus rm -rf "/var/lib/argus/backups/$stamp"
    Ok 'app database, index and packs'
} else { Bad 'argus backup failed' }
$dump = Join-Path $out 'litellm.dump'
cmd /c "docker exec postgres pg_dump -U $(EnvGet 'LLM_PG_USER' 'llmservice') -d litellm -Fc > `"$dump`""
if ($LASTEXITCODE -eq 0) { Ok 'gateway database' } else { Bad 'pg_dump failed' }
Copy-Item .env (Join-Path $out 'env')
Ok '.env (holds the secrets: keep this backup private)'
Get-ChildItem $out -Recurse -File | Where-Object Name -ne 'SHA256SUMS' | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), (Resolve-Path -Relative $_.FullName)
} | Set-Content (Join-Path $out 'SHA256SUMS')
if ($script:Failed -eq 0) {
    Get-ChildItem $dir -Directory -Filter 'argus-*' | Sort-Object Name -Descending | Select-Object -Skip $keep | Remove-Item -Recurse -Force
    Write-Host "done: $out (keeping the newest $keep)"
} else { Write-Host "$($script:Failed) part(s) failed; nothing older was removed" }
exit $script:Failed
