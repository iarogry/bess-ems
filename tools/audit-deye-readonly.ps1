param(
    [Parameter(Mandatory = $true)]
    [string] $EnvFile
)

$ErrorActionPreference = 'Stop'

function Read-DotEnv([string] $Path) {
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -notmatch '^\s*([^#][^=]*)=(.*)$') { continue }
        $key = $Matches[1].Trim()
        $value = $Matches[2].Trim().Trim('"').Trim("'")
        $values[$key] = $value
    }
    return $values
}

function Sha256-Lower([string] $Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    $hash = [Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Invoke-DeyeRead([string] $Name, [string] $Path, [hashtable] $Body, [string] $Token) {
    $allowed = @(
        '/station/latest',
        '/station/device',
        '/station/history',
        '/station/historyPower',
        '/device/latest',
        '/device/measurePoints',
        '/config/battery',
        '/config/system',
        '/config/tou'
    )
    if ($Path -notin $allowed) { throw "Endpoint is not on the read-only allowlist: $Path" }

    try {
        $response = Invoke-RestMethod `
            -Method Post `
            -Uri "$script:BaseUrl$Path" `
            -Headers @{ Authorization = "bearer $Token"; Accept = 'application/json' } `
            -ContentType 'application/json' `
            -Body ($Body | ConvertTo-Json -Depth 8 -Compress) `
            -TimeoutSec 30

        return $response
    }
    catch {
        $status = if ($_.Exception.Response) { [int] $_.Exception.Response.StatusCode } else { 0 }
        [pscustomobject]@{
            endpoint = $Name
            http = $status
            api_code = $null
            success = $false
            top_level_fields = @()
        } | ConvertTo-Json -Compress | Write-Output
        return $null
    }
}

$envMap = Read-DotEnv -Path $EnvFile
$script:BaseUrl = ($envMap['DeyeCloud__BaseUrl'] ?? 'https://eu1-developer.deyecloud.com/v1.0').TrimEnd('/')
$appId = $envMap['DeyeCloud__AppId']
$password = $envMap['DeyeCloud__Password']
$passwordHash = if ($password -match '^[a-fA-F0-9]{64}$') { $password.ToLowerInvariant() } else { Sha256-Lower $password }

$tokenBody = @{
    appSecret = $envMap['DeyeCloud__AppSecret']
    email = $envMap['DeyeCloud__Email']
    password = $passwordHash
}
if ($envMap['DeyeCloud__CompanyId']) { $tokenBody.companyId = [long] $envMap['DeyeCloud__CompanyId'] }

try {
    $tokenResponse = Invoke-RestMethod `
        -Method Post `
        -Uri "$script:BaseUrl/account/token?appId=$([Uri]::EscapeDataString($appId))" `
        -ContentType 'application/json' `
        -Body ($tokenBody | ConvertTo-Json -Compress) `
        -TimeoutSec 30
} catch {
    [pscustomobject]@{ endpoint = 'account/token'; http = 0; api_code = $null; success = $false } |
        ConvertTo-Json -Compress | Write-Output
    exit 2
}

$token = $tokenResponse.data.accessToken
if (-not $token) { $token = $tokenResponse.accessToken }
[pscustomobject]@{
    endpoint = 'account/token'
    http = 200
    api_code = [string] $tokenResponse.code
    success = [bool] $tokenResponse.success
    token_present = [bool] $token
} | ConvertTo-Json -Compress | Write-Output
if (-not $token) { exit 3 }

$stationIdRaw = $envMap['DeyeCloud__StationId']
$stationId = if ($stationIdRaw -match '^\d+$') { [long] $stationIdRaw } else { $stationIdRaw }

$latest = Invoke-DeyeRead -Name 'station/latest' -Path '/station/latest' -Body @{ stationId = $stationId } -Token $token
if ($latest) {
    [pscustomobject]@{
        endpoint = 'station/latest'
        http = 200
        api_code = [string] $latest.code
        success = [bool] $latest.success
        top_level_fields = @($latest.PSObject.Properties.Name | Sort-Object)
    } | ConvertTo-Json -Depth 4 -Compress | Write-Output
}

$yesterday = [DateTime]::UtcNow.Date.AddDays(-1).ToString('yyyy-MM-dd')
$history = Invoke-DeyeRead -Name 'station/history' -Path '/station/history' -Body @{
    stationId = $stationId
    startAt = $yesterday
    granularity = 1
} -Token $token
if ($history) {
    $historyItems = @($history.stationDataItems)
    $historyFields = @($historyItems | ForEach-Object { $_.PSObject.Properties.Name } | Sort-Object -Unique)
    [pscustomobject]@{
        endpoint = 'station/history-summary'
        http = 200
        api_code = [string] $history.code
        success = [bool] $history.success
        item_count = $historyItems.Count
        has_generation_energy = 'generationValue' -in $historyFields
        has_charge_energy = 'chargeValue' -in $historyFields
        has_discharge_energy = 'dischargeValue' -in $historyFields
    } | ConvertTo-Json -Compress | Write-Output
}

$historyEnd = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$historyStart = $historyEnd - 3600
$historyPower = Invoke-DeyeRead -Name 'station/historyPower' -Path '/station/historyPower' -Body @{
    stationId = $stationId
    startTimestamp = $historyStart
    endTimestamp = $historyEnd
} -Token $token
if ($historyPower) {
    [pscustomobject]@{
        endpoint = 'station/historyPower-summary'
        http = 200
        api_code = [string] $historyPower.code
        success = [bool] $historyPower.success
        item_count = @($historyPower.stationDataItems).Count
    } | ConvertTo-Json -Compress | Write-Output
}

$devices = Invoke-DeyeRead -Name 'station/device' -Path '/station/device' -Body @{ stationIds = @($stationId); page = 1; size = 20 } -Token $token
if ($devices) {
    $items = @($devices.deviceListItems)
    [pscustomobject]@{
        endpoint = 'station/device-summary'
        http = 200
        api_code = [string] $devices.code
        success = [bool] $devices.success
        device_count = $items.Count
        device_types = @($items.deviceType | Sort-Object -Unique)
    } | ConvertTo-Json -Depth 4 -Compress | Write-Output

    $inverter = $items | Where-Object { $_.deviceType -eq 'INVERTER' } | Select-Object -First 1
    if ($inverter) {
        $points = Invoke-DeyeRead -Name 'device/measurePoints' -Path '/device/measurePoints' -Body @{ deviceSn = $inverter.deviceSn; deviceType = 'INVERTER' } -Token $token
        if ($points) {
            [pscustomobject]@{
                endpoint = 'device/measurePoints-summary'
                http = 200
                api_code = [string] $points.code
                success = [bool] $points.success
                measure_point_count = @($points.measurePoints).Count
                energy_measure_points = @($points.measurePoints | Where-Object { $_ -match 'Generation|Yield|Charge.*Energy|Discharge.*Energy' } | Sort-Object -Unique)
                has_soc = [bool] (@($points.measurePoints) -match 'SOC|Capacity')
                has_total_charge = [bool] (@($points.measurePoints) -match 'Total.*Charge|Charge.*Energy')
                has_total_discharge = [bool] (@($points.measurePoints) -match 'Total.*Discharge|Discharge.*Energy')
            } | ConvertTo-Json -Compress | Write-Output
        }

        $deviceLatest = Invoke-DeyeRead -Name 'device/latest' -Path '/device/latest' -Body @{ deviceList = @($inverter.deviceSn) } -Token $token
        if ($deviceLatest) {
            $deviceData = @($deviceLatest.deviceDataList ?? $deviceLatest.data) | Select-Object -First 1
            [pscustomobject]@{
                endpoint = 'device/latest-summary'
                http = 200
                api_code = [string] $deviceLatest.code
                success = [bool] $deviceLatest.success
                metric_count = @($deviceData.dataList).Count
                collection_time_present = [bool] $deviceData.collectionTime
            } | ConvertTo-Json -Compress | Write-Output
        }

        foreach ($configName in @('battery', 'system', 'tou')) {
            $config = Invoke-DeyeRead -Name "config/$configName" -Path "/config/$configName" -Body @{ deviceSn = $inverter.deviceSn } -Token $token
            if ($config) {
                [pscustomobject]@{
                    endpoint = "config/$configName"
                    http = 200
                    api_code = [string] $config.code
                    success = [bool] $config.success
                    top_level_fields = @($config.PSObject.Properties.Name | Sort-Object)
                } | ConvertTo-Json -Depth 4 -Compress | Write-Output
                if ($configName -eq 'tou' -and $config.success) {
                    [pscustomobject]@{
                        endpoint = 'config/tou-detail'
                        tou_action = [string] $config.touAction
                        intervals = @($config.timeUseSettingItems | ForEach-Object {
                            [pscustomobject]@{
                                time = [string] $_.time
                                enable_generation = [bool] $_.enableGeneration
                                enable_grid_charge = [bool] $_.enableGridCharge
                                enable_sell = [bool] $_.enableSell
                                power = $_.power
                                soc = $_.soc
                                voltage = $_.voltage
                            }
                        })
                    } | ConvertTo-Json -Depth 6 -Compress | Write-Output
                }
            }
        }
    }
}
