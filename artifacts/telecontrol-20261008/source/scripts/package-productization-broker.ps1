param([Parameter(Mandatory = $true)][string]$DotnetPath)

# Local framework-dependent staging package only. Does not install/start a
# service, connect a database or acquire device credentials.
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$dotnet = (Resolve-Path -LiteralPath $DotnetPath).Path
$revision = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Cannot determine source revision." }
$sourceDirty = [bool](& git -C $repoRoot status --porcelain --untracked-files=normal)
$packageRoot = Join-Path $repoRoot ("artifacts\productization\broker-" + [Guid]::NewGuid().ToString("N"))
$payloadRoot = Join-Path $packageRoot "payload"
New-Item -ItemType Directory -Path $payloadRoot | Out-Null
$savedBuildServer = [Environment]::GetEnvironmentVariable("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", "Process")
try {
    [Environment]::SetEnvironmentVariable("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", "1", "Process")
    $project = Join-Path $repoRoot "src\host\BatteryEms.BrokerHost\BatteryEms.BrokerHost.csproj"
    & $dotnet restore $project /p:RestoreLockedMode=true /m:1 /nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed." }
    & $dotnet publish $project -c Release --no-restore --self-contained false /p:UseAppHost=false /m:1 /nodeReuse:false /p:UseSharedCompilation=false /warnaserror -o $payloadRoot
    if ($LASTEXITCODE -ne 0) { throw "Strict broker publish failed." }
} finally {
    [Environment]::SetEnvironmentVariable("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", $savedBuildServer, "Process")
}
Copy-Item -LiteralPath (Join-Path $repoRoot "docs\productization\broker-staging.example.json") -Destination $payloadRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "docs\productization\broker-staging-runbook.md") -Destination $payloadRoot
if (Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Filter "appsettings*.json") {
    throw "Refusing implicit runtime configuration in the staging payload."
}
$files = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($payloadRoot, $_.FullName).Replace('\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
$finalRevision = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $finalRevision -ne $revision) { throw "Source revision changed during packaging." }
$sourceDirty = $sourceDirty -or [bool](& git -C $repoRoot status --porcelain --untracked-files=normal)
$manifest = [ordered]@{
    schemaVersion = 1
    sourceRevision = $revision
    sourceDirty = $sourceDirty
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    sdkVersion = (& $dotnet --version).Trim()
    runtime = "Microsoft.AspNetCore.App 10.x; framework-dependent; no apphost"
    deviceWritesEnabled = $false
    files = $files
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageRoot "manifest.json") -Encoding utf8
$archive = $packageRoot + ".zip"
Compress-Archive -LiteralPath $payloadRoot, (Join-Path $packageRoot "manifest.json") -DestinationPath $archive
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
Write-Host "Broker package: $packageRoot"
Write-Host "Archive: $archive"
Write-Host "Archive SHA256: $archiveHash"
Write-Host "Source revision: $revision; dirty: $sourceDirty"
Write-Host "No service, database or device was started."
