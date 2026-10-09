param([Parameter(Mandatory=$true)][string]$EnvFile)
$ErrorActionPreference = 'Stop'

function Read-EnvMap($path) {
    $map = @{}
    Get-Content -LiteralPath $path | ForEach-Object {
        if ($_ -match '^\s*([^#][^=]*)=(.*)$') { $map[$Matches[1].Trim()] = $Matches[2].Trim().Trim('"').Trim("'") }
    }
    $map
}
function Get-Sha256([string]$value) {
    if ($value -match '^[a-fA-F0-9]{64}$') { return $value.ToLowerInvariant() }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value))).ToLowerInvariant()
}
function Normalize-Time([string]$value) {
    if ($value -match '^\d{4}$') { return $value.Substring(0,2) + ':' + $value.Substring(2,2) }
    $value
}

$envMap = Read-EnvMap $EnvFile
if ($envMap['DEYE_WRITE_ENABLED'] -ine 'true') { throw 'DEYE_WRITE_ENABLED must be true.' }
$master = $envMap['DeyeCloud__MasterSerial']
if ([string]::IsNullOrWhiteSpace($master)) { throw 'DeyeCloud__MasterSerial is required.' }
$base = ($envMap['DeyeCloud__BaseUrl'] ?? 'https://eu1-developer.deyecloud.com/v1.0').TrimEnd('/')
$tokenBody = @{ appSecret=$envMap['DeyeCloud__AppSecret']; email=$envMap['DeyeCloud__Email']; password=(Get-Sha256 $envMap['DeyeCloud__Password']) }
if ($envMap['DeyeCloud__CompanyId']) { $tokenBody.companyId = [long]$envMap['DeyeCloud__CompanyId'] }
$tokenResponse = Invoke-RestMethod -Method Post -Uri "$base/account/token?appId=$([Uri]::EscapeDataString($envMap['DeyeCloud__AppId']))" -ContentType 'application/json' -Body ($tokenBody | ConvertTo-Json -Compress)
$token = $tokenResponse.data.accessToken ?? $tokenResponse.accessToken
if (!$token) { throw 'Deye authentication returned no token.' }
$headers = @{ Authorization="Bearer $token"; Accept='application/json' }
function Invoke-DeyePost($path, $body) {
    Invoke-RestMethod -Method Post -Uri "$base$path" -Headers $headers -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 10 -Compress)
}

$tou = Invoke-DeyePost '/config/tou' @{ deviceSn=$master }
if (!$tou.success) { throw "TOU read failed: $($tou.msg)" }
$items = @($tou.timeUseSettingItems)
if ($items.Count -ne 6) { throw "Expected 6 TOU items, got $($items.Count)." }
$expectedTimes = @('18:00','19:00','20:00','22:00','23:00','00:00')
for ($i=0; $i -lt 6; $i++) {
    $items[$i].time = Normalize-Time ([string]$items[$i].time)
    if ($items[$i].time -ne $expectedTimes[$i]) { throw "Safety stop: unexpected TOU time at index $i." }
}
if ([int]$items[0].power -ne 80000 -or [int]$items[0].soc -ne 30 -or [int]$items[1].power -ne 0 -or [int]$items[1].soc -ne 90) {
    throw 'Safety stop: current Z4 no longer matches the audited pre-correction state.'
}

$desired = @(
    @{ power=0;     soc=100 },
    @{ power=65650; soc=30  },
    @{ power=80000; soc=30  },
    @{ power=0;     soc=30  },
    @{ power=0;     soc=30  },
    @{ power=80000; soc=30  }
)
for ($i=0; $i -lt 6; $i++) {
    $items[$i].enableGeneration = $true
    $items[$i].enableGridCharge = $false
    $items[$i].power = $desired[$i].power
    $items[$i].soc = $desired[$i].soc
    $items[$i].voltage = 290
}

$write = Invoke-DeyePost '/order/sys/tou/update' @{ deviceSn=$master; timeUseSettingItems=$items }
$orderId = $write.orderId ?? $write.data.orderId
if (!$orderId) { throw "Write rejected: code=$($write.code), success=$($write.success), msg=$($write.msg)" }
$deadline = (Get-Date).AddMinutes(5)
do {
    Start-Sleep -Seconds 2
    $orderResponse = Invoke-RestMethod -Method Get -Uri "$base/order/$orderId" -Headers $headers
    $order = if ($orderResponse.data) { $orderResponse.data } else { $orderResponse }
    $status = [int]$order.status
    if ($status -in 400,500) { throw "Deye order failed: $($order.error) $($order.msg)" }
} while ($status -ne 666 -and (Get-Date) -lt $deadline)
if ($status -ne 666) { throw 'Deye order timeout; no retry was attempted.' }

$readDeadline = (Get-Date).AddMinutes(5)
do {
    Start-Sleep -Seconds 5
    $verify = Invoke-DeyePost '/config/tou' @{ deviceSn=$master }
    $actual = @($verify.timeUseSettingItems)
    $matches = $actual.Count -eq 6
    for ($i=0; $matches -and $i -lt 6; $i++) {
        $matches = (Normalize-Time ([string]$actual[$i].time)) -eq $expectedTimes[$i] -and [int]$actual[$i].power -eq $desired[$i].power -and [int]$actual[$i].soc -eq $desired[$i].soc
    }
} while (!$matches -and (Get-Date) -lt $readDeadline)

[pscustomobject]@{ applied=$matches; terminal_status=$status; times=$expectedTimes; powers_w=@($desired.power); soc=@($desired.soc); repeat_write_attempted=$false } | ConvertTo-Json -Compress
if (!$matches) { exit 6 }
