param(
    [Parameter(Mandatory = $true)]
    [datetime]$From,

    [Parameter(Mandatory = $true)]
    [datetime]$To,

    [string]$EnvFile = ".env",

    [string]$OutFile = "",

    [string[]]$PointIds = @(),

    [int[]]$ParentIds = @(),

    [string]$SiteId = "",

    [string]$MeterConfigFile = "config/examples/site-balance.khlibzavod-5.json",

    [string]$CookieHeader = "",

    [string]$RefererUrl = "https://askue.net/show/content.html",

    [int]$PeriodSeconds = 1800,

    [ValidateSet(0, 30, 60)]
    [int]$IntervalMinutes = 0,

    [int]$MaxRequestsPerMinute = 30,

    [int]$DelayMs = 1000,

    [int]$JitterMs = 500,

    [int]$MaxRetries = 5,

    [int]$RetryBaseDelaySeconds = 10,

    [string]$TimeZoneId = "FLE Standard Time",

    [ValidateSet("Wide", "Long", "Both")]
    [string]$OutputFormat = "Wide",

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$invariantCulture = [System.Globalization.CultureInfo]::InvariantCulture
[System.Threading.Thread]::CurrentThread.CurrentCulture = $invariantCulture
[System.Threading.Thread]::CurrentThread.CurrentUICulture = $invariantCulture

if ($From.Date -gt $To.Date) {
    throw "From date must be less than or equal to To date."
}

if ($IntervalMinutes -ne 0) {
    $PeriodSeconds = $IntervalMinutes * 60
}

if ($PeriodSeconds -lt 300 -or $PeriodSeconds -gt 86400) {
    throw "PeriodSeconds must be between 300 and 86400."
}

if ($MaxRequestsPerMinute -lt 1) {
    throw "MaxRequestsPerMinute must be at least 1."
}

$envPath = if ([System.IO.Path]::IsPathRooted($EnvFile)) { $EnvFile } else { Join-Path (Get-Location) $EnvFile }
if (-not (Test-Path -LiteralPath $envPath)) {
    throw "Env file not found: $envPath"
}

function Read-DotEnv {
    param([string]$Path)

    $map = @{}
    Get-Content -LiteralPath $Path | ForEach-Object {
        if ($_ -match '^\s*#' -or $_ -notmatch '=') {
            return
        }

        $idx = $_.IndexOf('=')
        $key = $_.Substring(0, $idx).Trim()
        $value = $_.Substring($idx + 1).Trim()
        if ($value.Length -ge 2) {
            $first = $value[0]
            $last = $value[$value.Length - 1]
            if (($first -eq '"' -and $last -eq '"') -or ($first -eq "'" -and $last -eq "'")) {
                $value = $value.Substring(1, $value.Length - 2)
            }
        }

        $map[$key] = $value
    }

    return $map
}

function Require-EnvValue {
    param(
        [hashtable]$Map,
        [string]$Key
    )

    if (-not $Map.ContainsKey($Key) -or [string]::IsNullOrWhiteSpace($Map[$Key])) {
        throw "Required env value is missing: $Key"
    }

    return $Map[$Key]
}

function Get-OptionalEnvValue {
    param(
        [hashtable]$Map,
        [string]$Key
    )

    if (-not $Map.ContainsKey($Key) -or [string]::IsNullOrWhiteSpace($Map[$Key])) {
        return ""
    }

    return [string]$Map[$Key]
}

function Get-HttpStatusCode {
    param([object]$ErrorRecord)

    if ($null -eq $ErrorRecord) {
        return $null
    }

    $exception = $ErrorRecord.Exception
    if ($null -eq $exception) {
        return $null
    }

    $responseProperty = $exception.PSObject.Properties["Response"]
    if ($null -eq $responseProperty) {
        return $null
    }

    $response = $responseProperty.Value
    if ($null -eq $response) {
        return $null
    }

    $statusCodeProperty = $response.PSObject.Properties["StatusCode"]
    if ($null -eq $statusCodeProperty) {
        return $null
    }

    $statusCode = $statusCodeProperty.Value
    if ($statusCode -is [int]) {
        return [int]$statusCode
    }

    $valueProperty = $statusCode.PSObject.Properties["value__"]
    if ($null -eq $valueProperty) {
        return $null
    }

    return [int]$valueProperty.Value
}

function Invoke-AskueJson {
    param(
        [string]$Uri,
        [hashtable]$Headers,
        [ref]$LastRequestAt,
        [System.Random]$Random
    )

    $rpmDelayMs = [int][Math]::Ceiling(60000 / [double]$MaxRequestsPerMinute)
    $baseDelayMs = [Math]::Max($DelayMs, $rpmDelayMs)

    for ($attempt = 1; $attempt -le ($MaxRetries + 1); $attempt++) {
        if ($null -ne $LastRequestAt.Value) {
            $elapsedMs = ([DateTimeOffset]::UtcNow - $LastRequestAt.Value).TotalMilliseconds
            $jitter = if ($JitterMs -gt 0) { $Random.Next(0, $JitterMs + 1) } else { 0 }
            $sleepMs = $baseDelayMs + $jitter - [int]$elapsedMs
            if ($sleepMs -gt 0) {
                Start-Sleep -Milliseconds $sleepMs
            }
        }

        try {
            $LastRequestAt.Value = [DateTimeOffset]::UtcNow
            return Invoke-RestMethod `
                -Method Post `
                -Uri $Uri `
                -Headers $Headers `
                -Body "" `
                -ContentType "application/json" `
                -TimeoutSec 60
        }
        catch {
            $status = Get-HttpStatusCode $_
            $isRetryable = $status -eq 429 -or ($null -ne $status -and $status -ge 500 -and $status -lt 600) -or $null -eq $status
            if (-not $isRetryable -or $attempt -gt $MaxRetries) {
                throw
            }

            $backoffSeconds = $RetryBaseDelaySeconds * [Math]::Pow(2, $attempt - 1)
            $backoffSeconds += $Random.NextDouble() * [Math]::Max(1, $RetryBaseDelaySeconds)
            Write-Warning ("ASKUE request failed with status {0}; retry {1}/{2} after {3:n1}s." -f $status, $attempt, $MaxRetries, $backoffSeconds)
            Start-Sleep -Seconds $backoffSeconds
        }
    }
}

function Get-LocalDayBounds {
    param(
        [datetime]$Date,
        [TimeZoneInfo]$TimeZone
    )

    $localStart = [datetime]::SpecifyKind($Date.Date, [DateTimeKind]::Unspecified)
    $localEnd = $localStart.AddDays(1)
    $startOffset = New-Object DateTimeOffset($localStart, $TimeZone.GetUtcOffset($localStart))
    $endOffset = New-Object DateTimeOffset($localEnd, $TimeZone.GetUtcOffset($localEnd))

    return @{
        Start = $startOffset
        End = $endOffset
    }
}

function New-Buckets {
    param(
        [long]$StartSeconds,
        [long]$EndSeconds,
        [int]$Period
    )

    $buckets = [ordered]@{}
    for ($ts = $StartSeconds + $Period; $ts -le $EndSeconds; $ts += $Period) {
        $buckets[[string]$ts] = [ordered]@{
            timestamp = [DateTimeOffset]::FromUnixTimeSeconds($ts)
            apoz = $null
            aneg = $null
            ppoz = $null
            pneg = $null
        }
    }

    return $buckets
}

function Merge-Profile {
    param(
        [System.Collections.Specialized.OrderedDictionary]$Buckets,
        [object[]]$Profile,
        [long]$StartSeconds,
        [int]$Period,
        [string]$Key
    )

    foreach ($item in $Profile) {
        if ($null -eq $item.date -or $null -eq $item.value) {
            continue
        }

        $dateSeconds = [long]$item.date
        $intervalEnd = [long]([Math]::Ceiling(($dateSeconds - $StartSeconds) / [double]$Period) * $Period + $StartSeconds)
        $bucketKey = [string]$intervalEnd
        if (-not $Buckets.Contains($bucketKey)) {
            continue
        }

        $canonicalValue = Convert-AskueSourceValueToKiloUnitEnergy -Value $item.value
        $current = $Buckets[$bucketKey][$Key]
        if ($null -eq $current) {
            $Buckets[$bucketKey][$Key] = $canonicalValue
        }
        else {
            $Buckets[$bucketKey][$Key] = [double]$current + [double]$canonicalValue
        }
    }
}

function Convert-IntervalEnergyToPower {
    param(
        [Nullable[double]]$Energy,
        [int]$Period
    )

    if ($null -eq $Energy) {
        return $null
    }

    return [double]$Energy / ($Period / 3600.0)
}

function Convert-AskueSourceValueToKiloUnitEnergy {
    param([object]$Value)

    if ($null -eq $Value) {
        return $null
    }

    return [double]$Value
}

function Read-MeterConfig {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return @{}
    }

    $resolvedPath = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path (Get-Location) $Path }
    if (-not (Test-Path -LiteralPath $resolvedPath)) {
        throw "Meter config file not found: $resolvedPath"
    }

    $config = Get-Content -LiteralPath $resolvedPath -Raw | ConvertFrom-Json
    $map = @{}
    if ($null -eq $config.meters) {
        return $map
    }

    foreach ($meter in $config.meters) {
        if ($null -eq $meter.meter_id) {
            continue
        }

        $map[[string]$meter.meter_id] = [pscustomobject]@{
            meter_id = [string]$meter.meter_id
            configured_scale = if ($meter.PSObject.Properties.Name -contains "scale" -and $null -ne $meter.scale) { [double]$meter.scale } else { 1.0 }
            value_multiplier = if ($meter.PSObject.Properties.Name -contains "value_multiplier" -and $null -ne $meter.value_multiplier) { [double]$meter.value_multiplier } else { 1.0 }
        }
    }

    return $map
}

function Get-PointScale {
    param([object]$Point)

    if ($null -ne $Point.scale) {
        return [double]$Point.scale
    }

    return 1.0
}

function Get-MeterMultiplier {
    param(
        [object]$Point,
        [hashtable]$MeterConfigMap
    )

    $pointId = [string]$Point.id
    if ($MeterConfigMap.ContainsKey($pointId)) {
        return [double]$MeterConfigMap[$pointId].value_multiplier
    }

    return 1.0
}

function New-WideReading {
    param(
        [string]$SiteId,
        [object]$Point,
        [object]$Bucket,
        [DateTimeOffset]$TimestampUtc,
        [DateTimeOffset]$TimestampLocal,
        [int]$Period,
        [double]$ValueMultiplier
    )

    $intervalHours = $Period / 3600.0
    $rawActiveImport = $bucket.apoz
    $rawActiveExport = $bucket.aneg
    $rawReactiveImport = $bucket.ppoz
    $rawReactiveExport = $bucket.pneg

    $siteActiveImportKw = if ($null -ne $rawActiveImport) { [double]$rawActiveImport * $ValueMultiplier } else { $null }
    $siteActiveExportKw = if ($null -ne $rawActiveExport) { [double]$rawActiveExport * $ValueMultiplier } else { $null }
    $siteReactiveImportKvar = if ($null -ne $rawReactiveImport) { [double]$rawReactiveImport * $ValueMultiplier } else { $null }
    $siteReactiveExportKvar = if ($null -ne $rawReactiveExport) { [double]$rawReactiveExport * $ValueMultiplier } else { $null }

    $activeImportKwh = if ($null -ne $siteActiveImportKw) { $siteActiveImportKw * $intervalHours } else { $null }
    $activeExportKwh = if ($null -ne $siteActiveExportKw) { $siteActiveExportKw * $intervalHours } else { $null }
    $reactiveImportKvarh = if ($null -ne $siteReactiveImportKvar) { $siteReactiveImportKvar * $intervalHours } else { $null }
    $reactiveExportKvarh = if ($null -ne $siteReactiveExportKvar) { $siteReactiveExportKvar * $intervalHours } else { $null }

    $activeImportKw = Convert-IntervalEnergyToPower -Energy $activeImportKwh -Period $Period
    $activeExportKw = Convert-IntervalEnergyToPower -Energy $activeExportKwh -Period $Period
    $reactiveImportKvar = Convert-IntervalEnergyToPower -Energy $reactiveImportKvarh -Period $Period
    $reactiveExportKvar = Convert-IntervalEnergyToPower -Energy $reactiveExportKvarh -Period $Period
    $netActiveEnergyKwh = if ($null -ne $activeImportKwh -or $null -ne $activeExportKwh) { [double]($activeImportKwh ?? 0) - [double]($activeExportKwh ?? 0) } else { $null }
    $netActivePowerKw = Convert-IntervalEnergyToPower -Energy $netActiveEnergyKwh -Period $Period
    $sourceScale = Get-PointScale -Point $Point
    $signedActiveEnergyKwh = if ($null -ne $netActiveEnergyKwh) { $netActiveEnergyKwh * $sourceScale } else { $null }
    $signedActivePowerKw = if ($null -ne $netActivePowerKw) { $netActivePowerKw * $sourceScale } else { $null }
    $parent = if ($null -ne $Point.parent) { [string]$Point.parent } else { "" }
    $intervalStartLocal = $TimestampLocal.AddSeconds(-1 * $Period)
    $siteNetActiveKw = if ($null -ne $siteActiveImportKw -or $null -ne $siteActiveExportKw) { [double]($siteActiveImportKw ?? 0) - [double]($siteActiveExportKw ?? 0) } else { $null }

    return [pscustomobject]@{
        site_id = $SiteId
        source = "askue"
        instrument_type = "meter"
        instrument_id = [string]$Point.id
        instrument_name = [string]$Point.name
        group_parent_id = $parent
        source_scale = $sourceScale
        value_multiplier = $ValueMultiplier
        timestamp_utc = $TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ssK")
        timestamp_local = $TimestampLocal.ToString("yyyy-MM-ddTHH:mm:sszzz")
        interval_start_local = $intervalStartLocal.ToString("yyyy-MM-ddTHH:mm:sszzz")
        interval_label = "{0:HH:mm} - {1:HH:mm} ({1:dd.MM.yyyy})" -f $intervalStartLocal, $TimestampLocal
        interval_seconds = $Period
        quality = "measured"
        site_active_import_kw = $siteActiveImportKw
        site_active_export_kw = $siteActiveExportKw
        site_reactive_import_kvar = $siteReactiveImportKvar
        site_reactive_export_kvar = $siteReactiveExportKvar
        site_net_active_kw = $siteNetActiveKw
        active_energy_import_kwh = $activeImportKwh
        active_energy_export_kwh = $activeExportKwh
        active_power_import_kw = $activeImportKw
        active_power_export_kw = $activeExportKw
        net_active_energy_kwh = $netActiveEnergyKwh
        net_active_power_kw = $netActivePowerKw
        signed_active_energy_kwh = $signedActiveEnergyKwh
        signed_active_power_kw = $signedActivePowerKw
        reactive_energy_import_kvarh = $reactiveImportKvarh
        reactive_energy_export_kvarh = $reactiveExportKvarh
        reactive_power_import_kvar = $reactiveImportKvar
        reactive_power_export_kvar = $reactiveExportKvar
        raw_apoz = $rawActiveImport
        raw_aneg = $rawActiveExport
        raw_ppoz = $rawReactiveImport
        raw_pneg = $rawReactiveExport
    }
}

function Add-LongMetric {
    param(
        [System.Collections.Generic.List[object]]$Rows,
        [object]$Wide,
        [string]$Metric,
        [string]$Unit,
        [object]$Value
    )

    if ($null -eq $Value) {
        return
    }

    $Rows.Add([pscustomobject]@{
        site_id = $Wide.site_id
        source = $Wide.source
        instrument_type = $Wide.instrument_type
        instrument_id = $Wide.instrument_id
        instrument_name = $Wide.instrument_name
        group_parent_id = $Wide.group_parent_id
        source_scale = $Wide.source_scale
        value_multiplier = $Wide.value_multiplier
        timestamp_utc = $Wide.timestamp_utc
        timestamp_local = $Wide.timestamp_local
        interval_seconds = $Wide.interval_seconds
        quality = $Wide.quality
        metric = $Metric
        value = $Value
        unit = $Unit
    })
}

function Add-LongReadings {
    param(
        [System.Collections.Generic.List[object]]$Rows,
        [object]$Wide
    )

    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "active_energy_import" -Value $Wide.active_energy_import_kwh -Unit "kWh"
    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "active_energy_export" -Value $Wide.active_energy_export_kwh -Unit "kWh"
    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "site_active_import" -Value $Wide.site_active_import_kw -Unit "kW"
    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "site_active_export" -Value $Wide.site_active_export_kw -Unit "kW"
    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "active_power_import" -Value $Wide.active_power_import_kw -Unit "kW"
    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "active_power_export" -Value $Wide.active_power_export_kw -Unit "kW"
    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "net_active_energy" -Value $Wide.net_active_energy_kwh -Unit "kWh"
    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "net_active_power" -Value $Wide.net_active_power_kw -Unit "kW"
    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "signed_active_energy" -Value $Wide.signed_active_energy_kwh -Unit "kWh"
    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "signed_active_power" -Value $Wide.signed_active_power_kw -Unit "kW"
    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "reactive_energy_import" -Value $Wide.reactive_energy_import_kvarh -Unit "kVArh"
    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "reactive_energy_export" -Value $Wide.reactive_energy_export_kvarh -Unit "kVArh"
    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "reactive_power_import" -Value $Wide.reactive_power_import_kvar -Unit "kVAr"
    Add-LongMetric -Rows $Rows -Wide $Wide -Metric "reactive_power_export" -Value $Wide.reactive_power_export_kvar -Unit "kVAr"
}

$envValues = Read-DotEnv $envPath
$baseUrl = Require-EnvValue $envValues "Askue__BaseUrl"
$username = Require-EnvValue $envValues "Askue__Username"
$password = Require-EnvValue $envValues "Askue__Password"
$meterConfigMap = Read-MeterConfig -Path $MeterConfigFile

if ([string]::IsNullOrWhiteSpace($CookieHeader)) {
    $CookieHeader = Get-OptionalEnvValue -Map $envValues -Key "Askue__Cookie"
}

if ([string]::IsNullOrWhiteSpace($RefererUrl)) {
    $RefererUrl = "https://askue.net/show/content.html"
}

if ([string]::IsNullOrWhiteSpace($SiteId)) {
    if ($envValues.ContainsKey("Askue__SiteId") -and -not [string]::IsNullOrWhiteSpace($envValues["Askue__SiteId"])) {
        $SiteId = $envValues["Askue__SiteId"]
    }
    else {
        $SiteId = "default-site"
    }
}

if ($PointIds.Count -eq 0 -and $envValues.ContainsKey("Askue__PointIds") -and -not [string]::IsNullOrWhiteSpace($envValues["Askue__PointIds"])) {
    $PointIds = $envValues["Askue__PointIds"].Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() }
}

if ($ParentIds.Count -eq 0 -and $envValues.ContainsKey("Askue__ParentIds") -and -not [string]::IsNullOrWhiteSpace($envValues["Askue__ParentIds"])) {
    $ParentIds = @($envValues["Askue__ParentIds"].Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { [int]$_.Trim() })
}

if ([string]::IsNullOrWhiteSpace($OutFile)) {
    $safeFrom = $From.Date.ToString("yyyyMMdd")
    $safeTo = $To.Date.ToString("yyyyMMdd")
    $OutFile = Join-Path (Get-Location) ("askue-history-{0}-{1}.csv" -f $safeFrom, $safeTo)
}

if (-not $baseUrl.EndsWith("/")) {
    $baseUrl += "/"
}

$timeZone = [TimeZoneInfo]::FindSystemTimeZoneById($TimeZoneId)
$authBytes = [Text.Encoding]::UTF8.GetBytes("${username}:${password}")
$headers = @{
    Authorization = "Basic " + [Convert]::ToBase64String($authBytes)
    "User-Agent" = "bess-ems-askue-backfill/1.0"
    "Origin" = "https://askue.net"
    "Referer" = $RefererUrl
    "X-Requested-With" = "XMLHttpRequest"
}

if (-not [string]::IsNullOrWhiteSpace($CookieHeader)) {
    $headers["Cookie"] = $CookieHeader
}
$lastRequestAt = [ref]$null
$random = [System.Random]::new()

Write-Host ("Reading ASKUE point list from {0} ..." -f $baseUrl)
$pointsRaw = Invoke-AskueJson -Uri ($baseUrl + "points") -Headers $headers -LastRequestAt $lastRequestAt -Random $random
$points = if ($pointsRaw -is [array]) { $pointsRaw } else { @($pointsRaw) }
$selectedPoints = @($points)
if ($PointIds.Count -gt 0) {
    $pointSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($pointId in $PointIds) {
        [void]$pointSet.Add($pointId)
    }

    $selectedPoints = @($selectedPoints | Where-Object { $pointSet.Contains([string]$_.id) })
}

if ($ParentIds.Count -gt 0) {
    $parentSet = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($parentId in $ParentIds) {
        [void]$parentSet.Add($parentId)
    }

    $selectedPoints = @($selectedPoints | Where-Object { $null -ne $_.parent -and $parentSet.Contains([int]$_.parent) })
}
else {
    $selectedPoints = @($selectedPoints | Group-Object -Property id | ForEach-Object { $_.Group | Select-Object -First 1 })
}

if ($selectedPoints.Count -eq 0) {
    throw "No ASKUE points selected."
}

$days = @()
for ($date = $From.Date; $date -le $To.Date; $date = $date.AddDays(1)) {
    $days += $date
}

$plannedProfileRequests = $days.Count * $selectedPoints.Count * 4
Write-Host ("Selected {0} point membership(s) from {1} raw point records." -f $selectedPoints.Count, $points.Count)
Write-Host ("Backfill range: {0:yyyy-MM-dd}..{1:yyyy-MM-dd} inclusive, {2} day(s)." -f $From.Date, $To.Date, $days.Count)
Write-Host ("Planned profile requests: {0}; throttle: max {1}/min, base delay {2}ms + jitter 0..{3}ms." -f $plannedProfileRequests, $MaxRequestsPerMinute, $DelayMs, $JitterMs)

if ($DryRun) {
    Write-Host "DryRun enabled; no profile data was downloaded."
    return
}

$profileMap = @{
    1 = "apoz"
    2 = "aneg"
    3 = "ppoz"
    4 = "pneg"
}

$wideRows = New-Object System.Collections.Generic.List[object]
$longRows = New-Object System.Collections.Generic.List[object]
$totalRows = 0

foreach ($day in $days) {
    $bounds = Get-LocalDayBounds -Date $day -TimeZone $timeZone
    $startSeconds = $bounds.Start.ToUnixTimeSeconds()
    $endSeconds = $bounds.End.ToUnixTimeSeconds()
    Write-Host ("Downloading {0:yyyy-MM-dd}: {1} point membership(s) ..." -f $day, $selectedPoints.Count)

    foreach ($point in $selectedPoints) {
        $buckets = New-Buckets -StartSeconds $startSeconds -EndSeconds $endSeconds -Period $PeriodSeconds
        foreach ($profileId in 1, 2, 3, 4) {
            $uri = "{0}point/{1}/profile/{2}?b={3}&e={4}" -f $baseUrl, [Uri]::EscapeDataString([string]$point.id), $profileId, $startSeconds, $endSeconds
            $profileRaw = Invoke-AskueJson -Uri $uri -Headers $headers -LastRequestAt $lastRequestAt -Random $random
            $profile = if ($profileRaw -is [array]) { $profileRaw } else { @($profileRaw) }
            Merge-Profile -Buckets $buckets -Profile $profile -StartSeconds $startSeconds -Period $PeriodSeconds -Key $profileMap[$profileId]
        }

        foreach ($bucket in $buckets.Values) {
            if ($null -eq $bucket.apoz -and $null -eq $bucket.aneg -and $null -eq $bucket.ppoz -and $null -eq $bucket.pneg) {
                continue
            }

            $valueMultiplier = Get-MeterMultiplier -Point $point -MeterConfigMap $meterConfigMap
            $timestampUtc = $bucket.timestamp.ToUniversalTime()
            $timestampLocal = [TimeZoneInfo]::ConvertTime($bucket.timestamp, $timeZone)
            $wide = New-WideReading `
                -SiteId $SiteId `
                -Point $point `
                -Bucket $bucket `
                -TimestampUtc $timestampUtc `
                -TimestampLocal $timestampLocal `
                -Period $PeriodSeconds `
                -ValueMultiplier $valueMultiplier

            if ($OutputFormat -eq "Wide" -or $OutputFormat -eq "Both") {
                $wideRows.Add($wide)
            }

            if ($OutputFormat -eq "Long" -or $OutputFormat -eq "Both") {
                Add-LongReadings -Rows $longRows -Wide $wide
            }

            $totalRows++
        }
    }
}

$outPath = if ([System.IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path (Get-Location) $OutFile }
$outDir = Split-Path -Parent $outPath
if (-not [string]::IsNullOrWhiteSpace($outDir) -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir | Out-Null
}

if ($OutputFormat -eq "Wide") {
    $wideRows | Export-Csv -LiteralPath $outPath -NoTypeInformation -Encoding utf8
    Write-Host ("Saved {0} normalized wide readings to {1}" -f $wideRows.Count, $outPath)
}
elseif ($OutputFormat -eq "Long") {
    $longRows | Export-Csv -LiteralPath $outPath -NoTypeInformation -Encoding utf8
    Write-Host ("Saved {0} normalized long metric rows from {1} readings to {2}" -f $longRows.Count, $totalRows, $outPath)
}
else {
    $extension = [System.IO.Path]::GetExtension($outPath)
    $basePath = if ([string]::IsNullOrWhiteSpace($extension)) { $outPath } else { $outPath.Substring(0, $outPath.Length - $extension.Length) }
    $widePath = $basePath + ".wide.csv"
    $longPath = $basePath + ".long.csv"
    $wideRows | Export-Csv -LiteralPath $widePath -NoTypeInformation -Encoding utf8
    $longRows | Export-Csv -LiteralPath $longPath -NoTypeInformation -Encoding utf8
    Write-Host ("Saved {0} normalized wide readings to {1}" -f $wideRows.Count, $widePath)
    Write-Host ("Saved {0} normalized long metric rows to {1}" -f $longRows.Count, $longPath)
}
