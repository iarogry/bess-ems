param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$DotnetPath,
    [ValidateRange(1024, 65535)][int]$Port = 55441
)

# Starts ONLY the verified package with an empty process environment and all
# broker switches false. Owns its child processes; never stops a port owner.
$ErrorActionPreference = "Stop"
$packageRoot = (Resolve-Path -LiteralPath $PackagePath).Path
$dotnet = (Resolve-Path -LiteralPath $DotnetPath).Path
$payloadRoot = (Resolve-Path -LiteralPath (Join-Path $packageRoot "payload")).Path
$manifest = Get-Content -LiteralPath (Join-Path $packageRoot "manifest.json") -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.deviceWritesEnabled -ne $false) { throw "Unsupported or write-enabled package manifest." }
$actualFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File)
if ($actualFiles.Count -ne $manifest.files.Count) { throw "Unexpected payload file count." }
$seenPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    if ([IO.Path]::IsPathRooted($entry.path) -or !$seenPaths.Add($entry.path)) { throw "Invalid or duplicate manifest path." }
    $target = [IO.Path]::GetFullPath((Join-Path $payloadRoot $entry.path))
    if (!$target.StartsWith($payloadRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Manifest path escapes payload." }
    if ([IO.Path]::GetRelativePath($payloadRoot, $target).Replace('\', '/') -cne $entry.path) { throw "Manifest path must be canonical." }
    $file = Get-Item -LiteralPath $target
    if ($file.PSIsContainer -or $file.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Payload integrity failure: $($entry.path)" }
}
if (Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Filter "appsettings*.json") { throw "Implicit runtime configuration is forbidden." }
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $listener.Start() } finally { $listener.Stop() }
$resultRoot = Join-Path $packageRoot ("smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $resultRoot | Out-Null
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$client = [Net.Http.HttpClient]::new($handler, $true)
$client.BaseAddress = [Uri]::new("http://127.0.0.1:$Port/")
$client.Timeout = [TimeSpan]::FromSeconds(2)
$checks = @()
try {
    foreach ($cycle in 1..2) {
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $dotnet
        $start.WorkingDirectory = $payloadRoot
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.Environment.Clear()
        foreach ($name in @("SystemRoot", "WINDIR", "TEMP", "TMP")) {
            $value = [Environment]::GetEnvironmentVariable($name, "Process")
            if ($value) { $start.Environment[$name] = $value }
        }
        $start.Environment["DOTNET_ROOT"] = [IO.Path]::GetDirectoryName($dotnet)
        foreach ($arg in @("BatteryEms.BrokerHost.dll", "--Broker:Enabled=false", "--Broker:Deye:Enabled=false", "--Broker:Deye:WriteEnabled=false", "--urls=http://127.0.0.1:$Port/")) {
            $start.ArgumentList.Add($arg)
        }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        $started = $false
        $stdout = $null
        $stderr = $null
        $check = $null
        try {
            if (!$process.Start()) { throw "Could not start owned broker process." }
            $started = $true
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            $ready = $false
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
            while ([DateTimeOffset]::UtcNow -lt $deadline) {
                if ($process.HasExited) { throw "Owned broker exited during startup." }
                try {
                    $response = $client.GetAsync("healthz").GetAwaiter().GetResult()
                    try { $ready = [int]$response.StatusCode -eq 503 } finally { $response.Dispose() }
                } catch [Net.Http.HttpRequestException] { }
                if ($ready) { break }
                Start-Sleep -Milliseconds 100
            }
            if (!$ready) { throw "Disabled broker did not report expected 503." }
            $content = [Net.Http.StringContent]::new("{}", [Text.Encoding]::UTF8, "application/json")
            try { $write = $client.PostAsync("v1/device-writes", $content).GetAwaiter().GetResult() } finally { $content.Dispose() }
            try {
                if ([int]$write.StatusCode -ne 404) { throw "Disabled broker exposed a mutation route." }
            } finally { $write.Dispose() }
            if ($process.HasExited) { throw "Owned broker exited before checks completed." }
            $check = [ordered]@{ cycle = $cycle; processId = $process.Id; healthStatus = 503; mutationStatus = 404; loopbackPort = $Port; processStopped = $false }
            $checks += $check
        } finally {
            if ($started -and !$process.HasExited) {
                $process.Kill($true)
                if (!$process.WaitForExit(5000)) { throw "Owned broker did not stop; output collection aborted." }
            }
            if ($check) { $check.processStopped = $process.HasExited }
            if ($stdout) { [IO.File]::WriteAllText((Join-Path $resultRoot "cycle-$cycle.stdout.log"), $stdout.GetAwaiter().GetResult()) }
            if ($stderr) { [IO.File]::WriteAllText((Join-Path $resultRoot "cycle-$cycle.stderr.log"), $stderr.GetAwaiter().GetResult()) }
            $process.Dispose()
        }
    }
} finally { $client.Dispose() }
[ordered]@{ sourceRevision = $manifest.sourceRevision; sourceDirty = $manifest.sourceDirty; checkedAtUtc = [DateTimeOffset]::UtcNow.ToString("O"); checks = $checks } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $resultRoot "result.json") -Encoding utf8
Write-Host "Verified disabled package startup + owned-process restart: $resultRoot"
