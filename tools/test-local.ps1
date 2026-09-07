[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$x86 = 'C:\Program Files (x86)\dotnet\dotnet.exe'
$x64 = Join-Path $repo '.dotnet-x64\dotnet.exe'
$testProject = Join-Path $repo 'tests\adapters\driven\BatteryEms.Adapters.Optimization.Tests\BatteryEms.Adapters.Optimization.Tests.csproj'
$testDll = Join-Path $repo 'tests\adapters\driven\BatteryEms.Adapters.Optimization.Tests\bin\Debug\net10.0\BatteryEms.Adapters.Optimization.Tests.dll'
$vstest = Join-Path $repo '.dotnet-x64\sdk\10.0.400\vstest.console.dll'

if (!(Test-Path $x86) -or !(Test-Path $x64) -or !(Test-Path $vstest)) {
    throw 'Required: x86 .NET 10 SDK for build and local .dotnet-x64 SDK/runtime for native solver.'
}

& $x86 build $testProject --no-restore --disable-build-servers -m:1 /p:NuGetAudit=false /p:TreatWarningsAsErrors=false /v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$env:DOTNET_CLI_HOME = Join-Path $repo '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
& $x64 exec $vstest $testDll
exit $LASTEXITCODE
