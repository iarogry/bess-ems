param([Parameter(Mandatory=$true)][string]$EnvFile)
$ErrorActionPreference='Stop'
function EnvMap($p){$m=@{}; Get-Content -LiteralPath $p | % { if($_ -match '^\s*([^#][^=]*)=(.*)$'){$m[$Matches[1].Trim()]=$Matches[2].Trim().Trim('"').Trim("'")}}; $m}
function Sha([string]$v){if($v -match '^[a-fA-F0-9]{64}$'){return $v.ToLowerInvariant()}; [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($v))).ToLowerInvariant()}
$e=EnvMap $EnvFile
if($e['DEYE_WRITE_ENABLED'] -ine 'true'){throw 'DEYE_WRITE_ENABLED must be true for this explicit test.'}
if([string]::IsNullOrWhiteSpace($e['DeyeCloud__MasterSerial'])){throw 'DeyeCloud__MasterSerial is required.'}
$base=($e['DeyeCloud__BaseUrl'] ?? 'https://eu1-developer.deyecloud.com/v1.0').TrimEnd('/')
$h=@{Accept='application/json'}
$tokenBody=@{appSecret=$e['DeyeCloud__AppSecret'];email=$e['DeyeCloud__Email'];password=(Sha $e['DeyeCloud__Password'])}
if($e['DeyeCloud__CompanyId']){$tokenBody.companyId=[long]$e['DeyeCloud__CompanyId']}
$tokenResponse=Invoke-RestMethod -Method Post -Uri "$base/account/token?appId=$([Uri]::EscapeDataString($e['DeyeCloud__AppId']))" -ContentType 'application/json' -Body ($tokenBody|ConvertTo-Json -Compress)
$tok=$tokenResponse.data.accessToken; if(!$tok){$tok=$tokenResponse.accessToken}
if(!$tok){throw 'Deye authentication returned no token.'}; $h.Authorization="Bearer $tok"
function Post($path,$body){Invoke-RestMethod -Method Post -Uri "$base$path" -Headers $h -ContentType 'application/json' -Body ($body|ConvertTo-Json -Depth 10 -Compress)}
$master=$e['DeyeCloud__MasterSerial']
$tou=Post '/config/tou' @{deviceSn=$master}
if(!$tou.success){throw "TOU read failed: $($tou.msg)"}
$items=@($tou.timeUseSettingItems)
if($items.Count -ne 6){throw "Expected 6 TOU items, got $($items.Count)."}
$before=$items[0].power
if([int]$before -ne 80000){throw "Safety stop: first item power is $before W, expected 80000 W."}
$items | % { if($_.time -match '^\d{4}$'){ $_.time = $_.time.Substring(0,2)+':'+$_.time.Substring(2,2) } }
$items[0].power=79000
$write=Post '/order/sys/tou/update' @{deviceSn=$master;timeUseSettingItems=$items}
$orderId=$write.orderId; if(!$orderId -and $write.data){$orderId=$write.data.orderId}; if(!$orderId){throw "Write rejected: code=$($write.code), success=$($write.success), msg=$($write.msg)"}
$deadline=(Get-Date).AddMinutes(5); $status=0
do { Start-Sleep -Seconds 2; $result=Invoke-RestMethod -Method Get -Uri "$base/order/$orderId" -Headers $h; $r=if($result.data){$result.data}else{$result}; $status=[int]$r.status; if($status -in 400,500){throw "Deye order failed: $($r.error) $($r.msg)"} } while($status -ne 666 -and (Get-Date) -lt $deadline)
if($status -ne 666){throw "Deye order timeout: $orderId"}
$verify=Post '/config/tou' @{deviceSn=$master}; $v=@($verify.timeUseSettingItems)[0].power
if([int]$v -ne 79000){throw "Read-back mismatch: first item power is $v W."}
[pscustomobject]@{changed=$true;first_interval=[string]$items[0].time;old_power_kw=([int]$before/1000);new_power_kw=([int]$v/1000);order_id=[string]$orderId;status=$status} | ConvertTo-Json -Compress
