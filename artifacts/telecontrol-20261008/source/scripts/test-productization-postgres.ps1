param(
    [Parameter(Mandatory = $true)][string]$PostgresBin,
    [Parameter(Mandatory = $true)][string]$DotnetPath,
    [ValidateRange(1024, 65535)][int]$Port = 55439,
    [string]$Filter = ""
)

# Never connects tests to an existing cluster. Every run owns a new data
# directory/database, binds IPv4 loopback only, and stops that cluster finally.
# Integration tests reset schemas: do not replace this with operational .env.
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$binRoot = (Resolve-Path -LiteralPath $PostgresBin).Path
$dotnet = (Resolve-Path -LiteralPath $DotnetPath).Path
foreach ($name in @("initdb.exe", "pg_ctl.exe", "createdb.exe", "postgres.exe")) {
    if (-not (Test-Path -LiteralPath (Join-Path $binRoot $name) -PathType Leaf)) {
        throw "Missing portable PostgreSQL executable: $name"
    }
}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $listener.Start() } finally { $listener.Stop() }

$artifacts = Join-Path $repoRoot "artifacts\postgres-portable"
$cluster = [IO.Path]::GetFullPath((Join-Path $artifacts ("cluster-" + [Guid]::NewGuid().ToString("N"))))
if (-not $cluster.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Test cluster must be within this checkout."
}
if (Test-Path -LiteralPath $cluster) { throw "Refusing to reuse a data directory." }
New-Item -ItemType Directory -Path $cluster | Out-Null
$database = "bessems_productization_test"
$testUser = "bessems"
$logPath = Join-Path $cluster "postgres.log"
$resultsPath = Join-Path $cluster "test-results"
$envNames = @("POSTGRES_HOST", "POSTGRES_PORT", "POSTGRES_DB", "POSTGRES_USER", "POSTGRES_PASSWORD", "DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER")
$savedEnvironment = @{}
foreach ($name in $envNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process") }
$initialized = $false
$testExitCode = 1

try {
    & (Join-Path $binRoot "postgres.exe") --version
    # Trust authentication is ONLY for this disposable, loopback-only cluster;
    # no operational credentials or configuration are read or written.
    & (Join-Path $binRoot "initdb.exe") -D $cluster -U $testUser --auth=trust --encoding=UTF8 --no-locale
    if ($LASTEXITCODE -ne 0) { throw "initdb failed ($LASTEXITCODE)." }
    $initialized = $true
    & (Join-Path $binRoot "pg_ctl.exe") -D $cluster -l $logPath -o "-p $Port -c listen_addresses=127.0.0.1" -w start
    if ($LASTEXITCODE -ne 0) { throw "Test cluster startup failed ($LASTEXITCODE)." }
    & (Join-Path $binRoot "createdb.exe") -h 127.0.0.1 -p $Port -U $testUser $database
    if ($LASTEXITCODE -ne 0) { throw "Test database creation failed ($LASTEXITCODE)." }

    [Environment]::SetEnvironmentVariable("POSTGRES_HOST", "127.0.0.1", "Process")
    [Environment]::SetEnvironmentVariable("POSTGRES_PORT", [string]$Port, "Process")
    [Environment]::SetEnvironmentVariable("POSTGRES_DB", $database, "Process")
    [Environment]::SetEnvironmentVariable("POSTGRES_USER", $testUser, "Process")
    [Environment]::SetEnvironmentVariable("POSTGRES_PASSWORD", "bessems", "Process")
    [Environment]::SetEnvironmentVariable("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", "1", "Process")
    $project = Join-Path $repoRoot "tests\integration\BatteryEms.Persistence.IntegrationTests\BatteryEms.Persistence.IntegrationTests.csproj"
    $testArguments = @("test", $project, "--no-restore", "/m:1", "/nodeReuse:false", "/p:UseSharedCompilation=false", "/p:RunAnalyzers=false", "--logger", "trx;LogFileName=postgres-integration.trx", "--results-directory", $resultsPath)
    if ($Filter) { $testArguments += @("--filter", $Filter) }
    & $dotnet @testArguments
    $testExitCode = $LASTEXITCODE
}
finally {
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], "Process") }
    if ($initialized) {
        & (Join-Path $binRoot "pg_ctl.exe") -D $cluster status *> $null
        if ($LASTEXITCODE -eq 0) {
            & (Join-Path $binRoot "pg_ctl.exe") -D $cluster -m fast -w stop
            if ($LASTEXITCODE -ne 0) { throw "Could not stop owned test cluster: $cluster" }
        }
    }
    Write-Host "Preserved isolated test data/logs/results: $cluster"
}
exit $testExitCode
