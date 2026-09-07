param(
    [string]$EnvFile = ".env",
    [string]$PostgresBin = "C:\Program Files\Odoo 18.0.20250708\PostgreSQL\bin",
    [switch]$AllowNonTestDatabase
)

$ErrorActionPreference = "Stop"

function Read-DotEnv([string]$Path) {
    $values = @{}
    if (-not (Test-Path $Path)) {
        return $values
    }

    Get-Content $Path | ForEach-Object {
        $line = $_.Trim()
        if ($line -and -not $line.StartsWith("#") -and $line.Contains("=")) {
            $idx = $line.IndexOf("=")
            $key = $line.Substring(0, $idx).Trim()
            $value = $line.Substring($idx + 1).Trim().Trim('"')
            $values[$key] = $value
        }
    }
    return $values
}

$envPath = Join-Path (Resolve-Path ".") $EnvFile
$vars = Read-DotEnv $envPath

$hostName = $vars["POSTGRES_HOST"] ?? "127.0.0.1"
$port = $vars["POSTGRES_PORT"] ?? "5432"
$database = $vars["POSTGRES_DB"] ?? "bessems_test"
$user = $vars["POSTGRES_USER"] ?? "postgres"
$password = $vars["POSTGRES_PASSWORD"] ?? ""

if (-not $AllowNonTestDatabase -and $database -notmatch "(?i)test") {
    throw "Refusing to reset database '$database' because its name does not contain 'test'. Pass -AllowNonTestDatabase to override."
}

$psql = Join-Path $PostgresBin "psql.exe"
$createdb = Join-Path $PostgresBin "createdb.exe"
$pgIsReady = Join-Path $PostgresBin "pg_isready.exe"
foreach ($tool in @($psql, $createdb, $pgIsReady)) {
    if (-not (Test-Path $tool)) {
        throw "Postgres tool not found: $tool"
    }
}

$env:PGHOST = $hostName
$env:PGPORT = $port
$env:PGUSER = $user
$env:PGPASSWORD = $password

& $pgIsReady -h $hostName -p $port -U $user | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Postgres is not ready at $hostName`:$port for user '$user'."
}

$exists = (& $psql -h $hostName -p $port -U $user -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$database';").Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Could not query postgres database list."
}

if ($exists -ne "1") {
    & $createdb -h $hostName -p $port -U $user $database
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create database '$database'."
    }
}

$resetSql = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public; GRANT ALL ON SCHEMA public TO $user; GRANT ALL ON SCHEMA public TO public;"
& $psql -h $hostName -p $port -U $user -d $database -v ON_ERROR_STOP=1 -c $resetSql | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not reset schema in '$database'."
}

[pscustomobject]@{
    Host = $hostName
    Port = $port
    Database = $database
    User = $user
    Reset = "public schema dropped and recreated"
}
