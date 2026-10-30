# Run this in PowerShell terminal 4, after the 3 services have started.
# Tests: normal COMMIT, Hotel NO -> ABORT, ACTIVE expiry, lost ACK -> retry.
[CmdletBinding()]
param(
    [string]$CoordinatorUrl = 'http://localhost:5000',
    [string]$HotelUrl = 'http://localhost:5102',
    [string]$AirlineUrl = 'http://localhost:5101'
)

$ErrorActionPreference = 'Stop'
$coord = $CoordinatorUrl.TrimEnd('/')
$hotel = $HotelUrl.TrimEnd('/')

function Post([string]$url) { Invoke-RestMethod -Method Post -Uri $url }
function Get([string]$url) { Invoke-RestMethod -Method Get -Uri $url }
function New-Tx() { return Post "$coord/api/transactions" }
function Require-State([object]$tx, [string]$expected) {
    if ($tx.status -ne $expected) {
        throw "Expected $expected, received $($tx.status) for $($tx.transactionId)"
    }
    Write-Host "PASS: $expected - $($tx.transactionId)" -ForegroundColor Green
}
function Wait-State([string]$id, [string]$expected, [int]$timeoutSec = 20) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $tx = Get "$coord/api/transactions/$id"
        if ($tx.status -eq $expected) { Require-State $tx $expected; return }
        Start-Sleep -Seconds 1
    }
    throw "Timeout waiting for $expected on $id"
}

# Reset development switches in case a previous manual test left them changed.
Post "$coord/dev/notifications/RESUME" | Out-Null
foreach ($participant in @($AirlineUrl.TrimEnd('/'), $hotel)) {
    Post "$participant/dev/vote/YES" | Out-Null
    Post "$participant/dev/commit/OK" | Out-Null
    Post "$participant/dev/prepare-delay/0" | Out-Null
}

Write-Host 'TEST 1: Both YES => COMMITTED'
$tx = New-Tx
$res = Post "$coord/api/transactions/$($tx.transactionId)/end"
Require-State $res 'COMMITTED'

Write-Host 'TEST 2: Hotel NO => ABORTED'
try {
    Post "$hotel/dev/vote/NO" | Out-Null
    $tx = New-Tx
    $res = Post "$coord/api/transactions/$($tx.transactionId)/end"
    Require-State $res 'ABORTED'
}
finally { Post "$hotel/dev/vote/YES" | Out-Null }

Write-Host 'TEST 3: ACTIVE expires => cleanup => ABORTED'
$tx = New-Tx
Post "$coord/dev/transactions/$($tx.transactionId)/expire" | Out-Null
Wait-State $tx.transactionId 'ABORTED'

Write-Host 'TEST 4: Hotel applies COMMIT then loses ACK => retry'
try {
    Post "$hotel/dev/commit/APPLY_THEN_FAIL" | Out-Null
    $tx = New-Tx
    $res = Post "$coord/api/transactions/$($tx.transactionId)/end"
    Require-State $res 'COMMIT_DECIDED'
    $hotelTx = Get "$hotel/api/2pc/transactions/$($tx.transactionId)"
    if ($hotelTx.status -ne 'COMMITTED') { throw 'Hotel should be COMMITTED in this test' }
}
finally { Post "$hotel/dev/commit/OK" | Out-Null }
Wait-State $tx.transactionId 'COMMITTED' 25

Write-Host 'ALL SMOKE TESTS PASSED.' -ForegroundColor Green
