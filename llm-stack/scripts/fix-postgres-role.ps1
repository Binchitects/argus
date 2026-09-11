<#
.SYNOPSIS
    Make the role inside an existing Postgres volume match what .env names.

.DESCRIPTION
    Postgres reads POSTGRES_USER and POSTGRES_PASSWORD on FIRST INIT ONLY.
    After the data directory exists, changing them in .env changes nothing
    inside the database -- the role keeps whatever name and password it was
    born with. A stack can therefore run for months on credentials that .env
    does not describe, and the mismatch only becomes an outage when something
    forces a reconnect under the configured name.

    That is what happened here. Compose gives SHELL environment variables
    precedence over .env, an unrelated project had POSTGRES_USER and
    POSTGRES_PASSWORD set machine-wide, and the volume was initialised from
    those instead of from the generated secrets. The variables are now
    LLM_PG_USER and LLM_PG_PASSWORD, which collide with nothing; this script
    reconciles a database that predates the rename.

    Idempotent: if the role already exists it is left alone.

.EXAMPLE
    .\scripts\fix-postgres-role.ps1
.EXAMPLE
    .\scripts\fix-postgres-role.ps1 -LockOld
.EXAMPLE
    .\scripts\fix-postgres-role.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [switch]$LockOld,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

function Write-Ok   { param($m) Write-Host "  ok    $m"   -ForegroundColor Green }
function Write-Warn { param($m) Write-Host "  warn  $m"   -ForegroundColor Yellow }
function Stop-With  { param($m) Write-Host "  fail  $m"   -ForegroundColor Red; exit 1 }

if (-not (Test-Path '.env')) { Stop-With '.env not found - run scripts/bootstrap first.' }

# Read the target from .env DIRECTLY, never from the environment. A stray shell
# variable is the thing this script exists to undo, so trusting the environment
# would defeat the point. Get-Content already splits on CRLF, so no carriage
# return survives into the value.
$envLines = Get-Content '.env'
function Get-EnvValue {
    param($name)
    $hit = $envLines | Where-Object { $_ -match "^$name=" } | Select-Object -Last 1
    if ($hit) { $hit.Substring($name.Length + 1).Trim() } else { '' }
}

$targetUser = Get-EnvValue 'LLM_PG_USER'
$targetPass = Get-EnvValue 'LLM_PG_PASSWORD'
if (-not $targetUser) { $targetUser = 'llmservice' }

if (-not $targetPass) { Stop-With 'LLM_PG_PASSWORD is empty in .env - run scripts/bootstrap to generate one.' }
if ($targetPass -eq 'change-me-please') { Stop-With 'LLM_PG_PASSWORD is still the placeholder - run scripts/bootstrap.' }

$running = docker compose ps --status running --format '{{.Service}}'
if ($running -notcontains 'postgres') { Stop-With 'the postgres container is not running - start it first.' }

# Who is the database ACTUALLY running as? The bootstrap superuser is the one
# name guaranteed to be able to log in, and it need not match .env or compose.
$containerEnv = docker inspect postgres --format '{{range .Config.Env}}{{println .}}{{end}}'
$currentUser = ($containerEnv | Where-Object { $_ -match '^POSTGRES_USER=' } |
                Select-Object -First 1) -replace '^POSTGRES_USER=', ''
$currentUser = $currentUser.Trim()
if (-not $currentUser) { Stop-With 'could not read POSTGRES_USER from the running container.' }

Write-Host ''
Write-Host "  database is running as : $currentUser"
Write-Host "  .env asks for          : $targetUser"
Write-Host ''

function Invoke-Psql {
    param($sql)
    docker exec -i postgres psql -U $currentUser -d postgres -tAc $sql
}

if ($currentUser -eq $targetUser) {
    Write-Ok 'names already agree; nothing to rename.'
} else {
    Write-Warn 'names disagree - this is the mismatch being fixed.'
}

$exists   = (Invoke-Psql "select 1 from pg_roles where rolname = '$targetUser'") -join ''
$roleWork = ($exists.Trim() -ne '1')
if (-not $roleWork) { Write-Ok "role '$targetUser' already exists; leaving it alone." }

$dbs = ((Invoke-Psql @"
select string_agg(datname, ' ') from pg_database
 where datname in ('litellm','langfuse') and pg_get_userbyid(datdba) <> '$targetUser'
"@) -join '').Trim()

# Ask the DATABASE which other superusers can still log in, rather than reading
# it off the container. Once postgres has been recreated under the new name the
# container no longer remembers the old one, and comparing the two names finds
# nothing -- while the stale role, and the foreign password on it, are still
# there. The database is the only honest source for this.
$stale = ((Invoke-Psql @"
select string_agg(rolname, ' ') from pg_roles
 where rolsuper and rolcanlogin and rolname <> '$targetUser' and rolname not like 'pg_%'
"@) -join '').Trim()

if ($DryRun) {
    Write-Host '  --- plan ---'
    if ($roleWork) { Write-Host "  create role $targetUser (superuser, login, password from .env)" }
    if ($dbs)      { Write-Host "  transfer ownership to ${targetUser}: $dbs" }
    if ($stale) {
        if ($LockOld) { Write-Host "  revoke login from: $stale" }
        else          { Write-Host "  leave able to log in: $stale  (-LockOld revokes)" }
    }
    Write-Host '  (nothing written)'
    exit 0
}

# Always dump before touching roles. A wrong move here costs every API key,
# every spend record and the whole Langfuse history.
if (-not (Test-Path 'backups')) { New-Item -ItemType Directory 'backups' | Out-Null }
$backup = "backups/pg-role-fix-$(Get-Date -Format 'yyyyMMdd-HHmmss').sql"
docker exec postgres pg_dumpall -U $currentUser | Out-File -FilePath $backup -Encoding utf8
if (-not (Test-Path $backup) -or (Get-Item $backup).Length -eq 0) {
    Stop-With 'the pre-change dump is empty - refusing to continue.'
}
Write-Ok "backed up to $backup ($((Get-Item $backup).Length) bytes)"

# The password goes in on STDIN, never as an argument: arguments are visible to
# anything that can list processes in the container.
$statements = New-Object System.Collections.Generic.List[string]
if ($roleWork) {
    $statements.Add("CREATE ROLE $targetUser WITH LOGIN SUPERUSER PASSWORD '$targetPass';")
} else {
    $statements.Add("ALTER ROLE $targetUser WITH LOGIN SUPERUSER PASSWORD '$targetPass';")
}
foreach ($db in ($dbs -split '\s+' | Where-Object { $_ })) {
    $statements.Add("ALTER DATABASE $db OWNER TO $targetUser;")
}
$statements -join "`n" | docker exec -i postgres psql -U $currentUser -d postgres -v ON_ERROR_STOP=1 -q

Write-Ok "role '$targetUser' present with the password from .env"
if ($dbs) { Write-Ok "ownership transferred: $dbs" }

# Prove it before claiming it. A role that exists but cannot authenticate is
# the same outage wearing a different hat.
#
# -e PGPASSWORD is NOT used: that would put the password in the docker command's
# own argv, readable by anything listing processes ON THE HOST. It arrives on
# stdin instead and never leaves this pipe.
$probe = 'read -r pw; PGPASSWORD="$pw" psql -U "$1" -h 127.0.0.1 -d litellm -tAc "select 1"'
$targetPass | docker exec -i postgres sh -c $probe _ $targetUser *> $null
if ($LASTEXITCODE -ne 0) {
    Stop-With "'$targetUser' still cannot log in - restore from $backup"
}
Write-Ok "verified: '$targetUser' can authenticate over TCP against the litellm database"

if (-not $stale) {
    Write-Ok 'no other superuser can log in.'
} elseif ($LockOld) {
    foreach ($r in ($stale -split '\s+' | Where-Object { $_ })) {
        Invoke-Psql "ALTER ROLE ""$r"" NOLOGIN" | Out-Null
        Write-Ok "revoked login from '$r' (undo: ALTER ROLE ""$r"" LOGIN)"
    }
} else {
    Write-Warn "still able to log in, on passwords that are not in .env: $stale"
    Write-Warn 're-run with -LockOld once the stack is confirmed healthy.'
}

Write-Host ''
Write-Host '  Recreate the services that hold a connection string, so they pick'
Write-Host '  up the new credentials. A restart is not enough - it reuses the'
Write-Host '  old environment:'
Write-Host ''
Write-Host '    docker compose up -d --force-recreate litellm grafana postgres'
Write-Host ''
