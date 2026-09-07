param(
    [string]$PostgresBin = "C:\Program Files\Odoo 18.0.20250708\PostgreSQL\bin",
    [string]$DataDir = ".postgres-test",
    [string]$HostName = "127.0.0.1",
    [int]$Port = 55432,
    [string]$Database = "bessems_test",
    [string]$User = "bessems",
    [string]$Password = "bessems"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedDataDir = Join-Path $repoRoot $DataDir
$initdb = Join-Path $PostgresBin "initdb.exe"
$pgCtl = Join-Path $PostgresBin "pg_ctl.exe"
$createdb = Join-Path $PostgresBin "createdb.exe"
$psql = Join-Path $PostgresBin "psql.exe"
$pgIsReady = Join-Path $PostgresBin "pg_isready.exe"

foreach ($tool in @($initdb, $pgCtl, $createdb, $psql, $pgIsReady)) {
    if (-not (Test-Path $tool)) {
        throw "Postgres tool not found: $tool"
    }
}

if (-not (Test-Path $resolvedDataDir)) {
    New-Item -ItemType Directory -Path $resolvedDataDir | Out-Null
    & $initdb -D $resolvedDataDir -U $User --auth=trust --encoding=UTF8 | Write-Host
}

$logPath = Join-Path $resolvedDataDir "postgres.log"
$statusOutput = & $pgCtl -D $resolvedDataDir status 2>&1
if ($LASTEXITCODE -ne 0) {
    & $pgCtl -D $resolvedDataDir -l $logPath -o "-p $Port -c listen_addresses=localhost" start | Write-Host
}

$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    & $pgIsReady -h $HostName -p $Port -U $User | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $ready = $true
        break
    }
    Start-Sleep -Milliseconds 500
}
if (-not $ready) {
    throw "Postgres did not become ready at $HostName`:$Port"
}

$env:PGHOST = $HostName
$env:PGPORT = [string]$Port
$env:PGUSER = $User
$env:PGPASSWORD = $Password

& $psql -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$Database'" | ForEach-Object {
    $script:dbExists = $_.Trim() -eq "1"
}
if (-not $script:dbExists) {
    & $createdb -h $HostName -p $Port -U $User $Database
}

& $psql -h $HostName -p $Port -U $User -d $Database -v ON_ERROR_STOP=1 -c "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public; GRANT ALL ON SCHEMA public TO $User; GRANT ALL ON SCHEMA public TO public;" | Write-Host

[pscustomobject]@{
    Host = $HostName
    Port = $Port
    Database = $Database
    User = $User
    Password = $Password
    ConnectionString = "Host=$HostName;Port=$Port;Database=$Database;Username=$User;Password=$Password;Include Error Detail=true"
}
