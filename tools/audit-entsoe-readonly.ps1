param(
    [Parameter(Mandatory = $true)]
    [string] $EnvFile,

    [int] $DaysOffset = 1,

    [string] $DomainCodeOverride = ''
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

$envMap = Read-DotEnv -Path $EnvFile
$baseUrl = $envMap['Bess__EntsoeApiBaseUrl']
$token = $envMap['Bess__EntsoeApiToken']
$domain = $envMap['Bess__EntsoeDomainCode']
if ($DomainCodeOverride) { $domain = $DomainCodeOverride }

try {
    $zone = [TimeZoneInfo]::FindSystemTimeZoneById('FLE Standard Time')
} catch {
    $zone = [TimeZoneInfo]::FindSystemTimeZoneById('Europe/Kyiv')
}

$localNow = [TimeZoneInfo]::ConvertTime([DateTimeOffset]::UtcNow, $zone)
$targetDay = $localNow.Date.AddDays($DaysOffset)
$dayAfter = $targetDay.AddDays(1)
$startUtc = [TimeZoneInfo]::ConvertTimeToUtc($targetDay, $zone)
$endUtc = [TimeZoneInfo]::ConvertTimeToUtc($dayAfter, $zone)

$parameters = @{
    securityToken = $token
    documentType = 'A44'
    in_Domain = $domain
    out_Domain = $domain
    periodStart = $startUtc.ToString('yyyyMMddHHmm')
    periodEnd = $endUtc.ToString('yyyyMMddHHmm')
}
$query = ($parameters.GetEnumerator() | ForEach-Object {
    "$([Uri]::EscapeDataString($_.Key))=$([Uri]::EscapeDataString([string] $_.Value))"
}) -join '&'

try {
    $response = Invoke-WebRequest -Method Get -Uri "$baseUrl`?$query" -TimeoutSec 60
    [xml] $document = $response.Content
    $timeSeries = @($document.SelectNodes("//*[local-name()='TimeSeries']"))
    $points = @($document.SelectNodes("//*[local-name()='Point']"))
    $currencyNodes = @($document.SelectNodes("//*[local-name()='currency_Unit.name']"))
    $measureNodes = @($document.SelectNodes("//*[local-name()='price_Measure_Unit.name']"))
    [pscustomobject]@{
        endpoint = 'entso-e/A44'
        http = [int] $response.StatusCode
        domain_code_present = [bool] $domain
        domain_is_ua_bzn = $domain -eq '10Y1001C--00003F'
        horizon_start_utc = ([DateTimeOffset] $startUtc).ToString('O')
        horizon_end_utc = ([DateTimeOffset] $endUtc).ToString('O')
        expected_hours = ($endUtc - $startUtc).TotalHours
        time_series_count = $timeSeries.Count
        point_count = $points.Count
        currencies = @($currencyNodes.InnerText | Sort-Object -Unique)
        measure_units = @($measureNodes.InnerText | Sort-Object -Unique)
    } | ConvertTo-Json -Depth 4 -Compress | Write-Output
} catch {
    $status = if ($_.Exception.Response) { [int] $_.Exception.Response.StatusCode } else { 0 }
    [pscustomobject]@{
        endpoint = 'entso-e/A44'
        http = $status
        domain_code_present = [bool] $domain
        domain_is_ua_bzn = $domain -eq '10Y1001C--00003F'
        success = $false
    } | ConvertTo-Json -Compress | Write-Output
    exit 2
}
