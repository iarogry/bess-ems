param(
    [Parameter(Mandatory = $true)]
    [string] $EnvFile,
    [int] $FreshnessLimitSeconds = 300
)

$ErrorActionPreference = 'Stop'

function Read-DotEnv([string] $Path) {
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -notmatch '^\s*([^#][^=]*)=(.*)$') { continue }
        $values[$Matches[1].Trim()] = $Matches[2].Trim().Trim('"').Trim("'")
    }
    return $values
}

function Sha256-Lower([string] $Value) {
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value))
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Invoke-Read([string] $Path, [hashtable] $Body, [string] $Token) {
    $allowed = @('/station/device', '/device/latest', '/config/tou', '/config/system')
    if ($Path -notin $allowed) { throw "Endpoint is not on the switch preflight allowlist." }
    $response = Invoke-RestMethod -Method Post -Uri "$script:BaseUrl$Path" `
        -Headers @{ Authorization = "bearer $Token"; Accept = 'application/json' } `
        -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 8 -Compress) -TimeoutSec 30
    if ([string]$response.code -notin @('0', '1000000')) { throw "Read endpoint returned a non-success code." }
    return $response
}

function Get-PointMap($Row) {
    $map = @{}
    foreach ($point in @($Row.dataList)) {
        if ($point.key) { $map[[string]$point.key] = [string]$point.value }
    }
    return $map
}

function Get-Number($Map, [string] $Key) {
    $parsed = 0.0
    if ($Map.ContainsKey($Key) -and [double]::TryParse($Map[$Key], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) { return $parsed }
    return $null
}

try {
    $envMap = Read-DotEnv -Path $EnvFile
    $script:BaseUrl = ($envMap['DeyeCloud__BaseUrl'] ?? 'https://eu1-developer.deyecloud.com/v1.0').TrimEnd('/')
    $password = $envMap['DeyeCloud__Password']
    $passwordHash = if ($password -match '^[a-fA-F0-9]{64}$') { $password.ToLowerInvariant() } else { Sha256-Lower $password }
    $tokenBody = @{ appSecret = $envMap['DeyeCloud__AppSecret']; email = $envMap['DeyeCloud__Email']; password = $passwordHash }
    if ($envMap['DeyeCloud__CompanyId']) { $tokenBody.companyId = [long]$envMap['DeyeCloud__CompanyId'] }
    $tokenResponse = Invoke-RestMethod -Method Post -Uri "$script:BaseUrl/account/token?appId=$([Uri]::EscapeDataString($envMap['DeyeCloud__AppId']))" `
        -ContentType 'application/json' -Body ($tokenBody | ConvertTo-Json -Compress) -TimeoutSec 30
    $token = [string]($tokenResponse.data.accessToken ?? $tokenResponse.accessToken)
    if ([string]::IsNullOrWhiteSpace($token)) { throw "Token response was empty." }

    $stationIdRaw = $envMap['DeyeCloud__StationId']
    $stationId = if ($stationIdRaw -match '^\d+$') { [long]$stationIdRaw } else { $stationIdRaw }
    $deviceResponse = Invoke-Read '/station/device' @{ stationIds = @($stationId); page = 1; size = 20 } $token
    $items = @($deviceResponse.deviceListItems)
    if ($items.Count -eq 0 -and $deviceResponse.data.deviceListItems) { $items = @($deviceResponse.data.deviceListItems) }
    $inverters = @($items | Where-Object { [string]$_.deviceType -eq 'INVERTER' })
    $masterSerial = [string]$envMap['DeyeCloud__MasterSerial']
    $master = $inverters | Where-Object { [string]$_.deviceSn -eq $masterSerial } | Select-Object -First 1
    $latest = Invoke-Read '/device/latest' @{ deviceList = @($inverters | ForEach-Object { [string]$_.deviceSn }) } $token
    $latestRows = @($latest.deviceDataList)
    if ($latestRows.Count -eq 0) { $latestRows = @($latest.data) }
    $nowEpoch = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $telemetry = @()
    foreach ($device in $inverters) {
        $row = $latestRows | Where-Object { [string]$_.deviceSn -eq [string]$device.deviceSn } | Select-Object -First 1
        $map = Get-PointMap $row
        $collection = if ($row.collectionTime) { [long]$row.collectionTime } elseif ($device.collectionTime) { [long]$device.collectionTime } else { 0 }
        $age = if ($collection -gt 0) { $nowEpoch - $collection } else { 999999 }
        $alarmPoints = @($map.Keys | Where-Object { $_ -match '(?i)alarm|fault|error|warning|outage' })
        $activeAlarm = $false
        foreach ($key in $alarmPoints) {
            $value = $map[$key]
            if ($value -notmatch '^(?i)(0|0\.0|false|none|normal|ok|no)$' -and $value -notmatch '^(?i)\s*$') { $activeAlarm = $true }
        }
        $telemetry += [ordered]@{
            online = ([string]$device.connectStatus -in @('1', 'true', 'True'))
            has_data = ($null -ne $row)
            fresh = ($age -ge 0 -and $age -le $FreshnessLimitSeconds)
            age_seconds = $age
            has_bms_voltage = ($null -ne (Get-Number $map 'BMSVoltage'))
            has_bms_soc = ($null -ne (Get-Number $map 'BMSSOC'))
            active_alarm = $activeAlarm
        }
    }
    $masterRow = $latestRows | Where-Object { [string]$_.deviceSn -eq $masterSerial } | Select-Object -First 1
    $masterMap = Get-PointMap $masterRow
    $masterVoltage = Get-Number $masterMap 'BMSVoltage'
    $masterSoc = Get-Number $masterMap 'BMSSOC'
    $masterCapacity = Get-Number $masterMap 'BatteryRatedCapacity'
    $masterTou = Invoke-Read '/config/tou' @{ deviceSn = $masterSerial } $token
    $masterSystem = Invoke-Read '/config/system' @{ deviceSn = $masterSerial } $token
    $result = [ordered]@{
        success = $true
        inverter_count = $inverters.Count
        expected_inverter_count = 2
        master_matches_configured_serial = ($null -ne $master)
        both_online = (@($telemetry | Where-Object { $_.online }).Count -eq 2)
        all_telemetry_fresh = (@($telemetry | Where-Object { $_.fresh }).Count -eq 2)
        no_active_alarm = (@($telemetry | Where-Object { $_.active_alarm }).Count -eq 0)
        telemetry = $telemetry
        master_bms_voltage_v = $masterVoltage
        master_bms_soc_percent = $masterSoc
        master_battery_rated_capacity_ah = $masterCapacity
        master_tou_readable = ([string]$masterTou.code -in @('0', '1000000'))
        master_system_readable = ([string]$masterSystem.code -in @('0', '1000000'))
    }
    $result.preflight_pass = ($result.inverter_count -eq 2 -and $result.master_matches_configured_serial -and $result.both_online -and $result.all_telemetry_fresh -and $result.no_active_alarm -and $null -ne $masterVoltage -and $null -ne $masterSoc -and $null -ne $masterCapacity -and $result.master_tou_readable -and $result.master_system_readable)
    $result | ConvertTo-Json -Depth 8 -Compress
    if (-not $result.preflight_pass) { exit 4 }
}
catch {
    [ordered]@{ success = $false; preflight_pass = $false; error = 'read_only_preflight_failed' } | ConvertTo-Json -Compress
    exit 5
}
