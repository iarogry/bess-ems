param(
    [Parameter(Mandatory = $true)]
    [datetime]$Date,

    [string]$ConfigFile = "config/examples/site-balance.khlibzavod-5.json",

    [string]$EnvFile = ".env",

    [int]$PeriodSeconds = 3600,

    [int]$DelayMs = 500,

    [string]$TimeZoneId = "FLE Standard Time"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$invariantCulture = [System.Globalization.CultureInfo]::InvariantCulture
[System.Threading.Thread]::CurrentThread.CurrentCulture = $invariantCulture
[System.Threading.Thread]::CurrentThread.CurrentUICulture = $invariantCulture
$env:HTTP_PROXY = ""
$env:HTTPS_PROXY = ""
$env:ALL_PROXY = ""
$env:GIT_HTTP_PROXY = ""
$env:GIT_HTTPS_PROXY = ""

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

function Invoke-AskueJson {
    param(
        [string]$Uri,
        [hashtable]$Headers
    )

    Start-Sleep -Milliseconds $DelayMs
    return Invoke-RestMethod `
        -Method Post `
        -Uri $Uri `
        -Headers $Headers `
        -Body "" `
        -ContentType "application/json" `
        -TimeoutSec 60
}

function Get-LocalDayBounds {
    param(
        [datetime]$Day,
        [TimeZoneInfo]$TimeZone
    )

    $localStart = [datetime]::SpecifyKind($Day.Date, [DateTimeKind]::Unspecified)
    $localEnd = $localStart.AddDays(1)
    return @{
        Start = New-Object DateTimeOffset($localStart, $TimeZone.GetUtcOffset($localStart))
        End = New-Object DateTimeOffset($localEnd, $TimeZone.GetUtcOffset($localEnd))
    }
}

function Sum-Profile {
    param(
        [object[]]$Profile,
        [double]$ValueMultiplier,
        [int]$DefaultPeriodSeconds
    )

    $total = 0.0
    $hasValue = $false
    foreach ($item in $Profile) {
        if ($null -eq $item.value) {
            continue
        }

        $stepSeconds = if ($item.PSObject.Properties.Name -contains "step" -and $null -ne $item.step) {
            [int]$item.step
        }
        else {
            $DefaultPeriodSeconds
        }

        $total += [double]$item.value * $ValueMultiplier
        $hasValue = $true
    }

    if ($hasValue) {
        return $total
    }

    return $null
}

function Add-Nullable {
    param(
        [double]$Left,
        [Nullable[double]]$Right
    )

    if ($null -eq $Right) {
        return $Left
    }

    return $Left + [double]$Right
}

$configPath = if ([System.IO.Path]::IsPathRooted($ConfigFile)) { $ConfigFile } else { Join-Path (Get-Location) $ConfigFile }
$envPath = if ([System.IO.Path]::IsPathRooted($EnvFile)) { $EnvFile } else { Join-Path (Get-Location) $EnvFile }
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "Site balance config not found: $configPath"
}

if (-not (Test-Path -LiteralPath $envPath)) {
    throw "Env file not found: $envPath"
}

$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$envValues = Read-DotEnv $envPath
$baseUrl = Require-EnvValue $envValues "Askue__BaseUrl"
$username = Require-EnvValue $envValues "Askue__Username"
$password = Require-EnvValue $envValues "Askue__Password"
if (-not $baseUrl.EndsWith("/")) {
    $baseUrl += "/"
}

$authBytes = [Text.Encoding]::UTF8.GetBytes("${username}:${password}")
$headers = @{
    Authorization = "Basic " + [Convert]::ToBase64String($authBytes)
    "User-Agent" = "bess-ems-site-balance-test/1.0"
}

$timeZone = [TimeZoneInfo]::FindSystemTimeZoneById($TimeZoneId)
$bounds = Get-LocalDayBounds -Day $Date -TimeZone $timeZone
$startSeconds = $bounds.Start.ToUnixTimeSeconds()
$endSeconds = $bounds.End.ToUnixTimeSeconds()

$meters = @($config.meters | Where-Object { $_.enabled -and $_.role -ne "disabled" -and $_.role -ne "unassigned" })
$rows = New-Object System.Collections.Generic.List[object]
foreach ($meter in $meters) {
    $apozUri = "{0}point/{1}/profile/1?b={2}&e={3}" -f $baseUrl, [Uri]::EscapeDataString([string]$meter.meter_id), $startSeconds, $endSeconds
    $anegUri = "{0}point/{1}/profile/2?b={2}&e={3}" -f $baseUrl, [Uri]::EscapeDataString([string]$meter.meter_id), $startSeconds, $endSeconds
    $apozRaw = Invoke-AskueJson -Uri $apozUri -Headers $headers
    $anegRaw = Invoke-AskueJson -Uri $anegUri -Headers $headers
    $valueMultiplier = if ($meter.PSObject.Properties.Name -contains "value_multiplier") { [double]$meter.value_multiplier } else { 1.0 }
    $apoz = Sum-Profile -Profile @($apozRaw) -ValueMultiplier $valueMultiplier -DefaultPeriodSeconds $PeriodSeconds
    $aneg = Sum-Profile -Profile @($anegRaw) -ValueMultiplier $valueMultiplier -DefaultPeriodSeconds $PeriodSeconds
    $rows.Add([pscustomobject]@{
        meter_id = [string]$meter.meter_id
        name = [string]$meter.name
        role = [string]$meter.role
        generation_type = if ($meter.PSObject.Properties.Name -contains "generation_type") { [string]$meter.generation_type } else { "" }
        value_multiplier = $valueMultiplier
        apoz_kwh = $apoz
        aneg_kwh = $aneg
    })
}

$mainImport = 0.0
$mainExport = 0.0
$subconsumer = 0.0
$generationByType = @{}
foreach ($row in $rows) {
    if ($row.role -eq "main_grid_meter") {
        $mainImport = Add-Nullable -Left $mainImport -Right $row.apoz_kwh
        $mainExport = Add-Nullable -Left $mainExport -Right $row.aneg_kwh
    }
    elseif ($row.role -eq "subconsumer_meter") {
        $subconsumer = Add-Nullable -Left $subconsumer -Right $row.apoz_kwh
    }
    elseif ($row.role -eq "generation_meter") {
        if (-not $generationByType.ContainsKey($row.generation_type)) {
            $generationByType[$row.generation_type] = [pscustomobject]@{
                generation_type = $row.generation_type
                auxiliary_consumption_kwh = 0.0
                export_kwh = 0.0
            }
        }

        $current = $generationByType[$row.generation_type]
        $current.auxiliary_consumption_kwh = Add-Nullable -Left $current.auxiliary_consumption_kwh -Right $row.apoz_kwh
        $current.export_kwh = Add-Nullable -Left $current.export_kwh -Right $row.aneg_kwh
    }
}

$auxiliary = @($generationByType.Values | ForEach-Object { $_.auxiliary_consumption_kwh } | Measure-Object -Sum).Sum
if ($null -eq $auxiliary) {
    $auxiliary = 0.0
}

$calculatedOwn = $mainImport - $subconsumer + $auxiliary
$own = [Math]::Max(0, $calculatedOwn)
$derivedExport = 0.0 # Negative residual is an anomaly, not measured export.
$gridExport = $mainExport
$siteNet = $mainImport - $gridExport
$missing = @($rows | Where-Object { $null -eq $_.apoz_kwh -and $null -eq $_.aneg_kwh })
$quality = if ($missing.Count -gt 0) { "incomplete" } else { "complete" }

$generationRows = @($generationByType.Values | Sort-Object generation_type | ForEach-Object {
    [pscustomobject]@{
        generation_type = $_.generation_type
        auxiliary_consumption_kwh = [Math]::Round($_.auxiliary_consumption_kwh, 6)
        export_kwh = [Math]::Round($_.export_kwh, 6)
        net_generation_kwh = [Math]::Round($_.export_kwh - $_.auxiliary_consumption_kwh, 6)
    }
})

[pscustomobject]@{
    site_id = $config.site_id
    date = $Date.ToString("yyyy-MM-dd")
    interval_start = $bounds.Start.ToString("yyyy-MM-ddTHH:mm:sszzz")
    interval_end = $bounds.End.ToString("yyyy-MM-ddTHH:mm:sszzz")
    data_quality_status = $quality
    main_grid_import_kwh = [Math]::Round($mainImport, 6)
    main_grid_export_kwh = [Math]::Round($mainExport, 6)
    subconsumer_consumption_kwh = [Math]::Round($subconsumer, 6)
    own_consumption_kwh = [Math]::Round($own, 6)
    derived_export_from_negative_consumption_kwh = [Math]::Round($derivedExport, 6)
    grid_import_kwh = [Math]::Round($mainImport, 6)
    grid_export_kwh = [Math]::Round($gridExport, 6)
    site_net_balance_kwh = [Math]::Round($siteNet, 6)
    generation = $generationRows
    missing_meters = @($missing | Select-Object meter_id, name, role)
    meter_totals = @($rows | Sort-Object role, meter_id)
} | ConvertTo-Json -Depth 8
