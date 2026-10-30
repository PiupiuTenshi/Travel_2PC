# Self-contained Student 1 checks. Uses temporary data and its own service ports.
[CmdletBinding()]
param([ValidateRange(1024, 65000)][int]$BasePort = 16000)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('travel2pc-student1-' + [guid]::NewGuid().ToString('N'))
$coordUrl = "http://localhost:$BasePort"
$airlineUrl = 'http://localhost:' + ($BasePort + 1)
$hotelUrl = 'http://localhost:' + ($BasePort + 2)
$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$pendingEnd = $null
$savedEnvironment = @{}
foreach ($name in @('ASPNETCORE_ENVIRONMENT', 'Logging__EventLog__LogLevel__Default')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Assert-Check([bool]$condition, [string]$message) {
    if (!$condition) { throw $message }
    Write-Host "PASS: $message" -ForegroundColor Green
}
function Post([string]$path) { Invoke-RestMethod -Method Post -Uri "$coordUrl$path" -TimeoutSec 15 }
function Read-Tx([string]$id) { Invoke-RestMethod -Uri "$coordUrl/api/transactions/$id" -TimeoutSec 10 }
function Wait-State([string]$id, [string]$status) {
    $deadline = (Get-Date).AddSeconds(35)
    do {
        $tx = Read-Tx $id
        if ($tx.status -eq $status) { return $tx }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "Expected $status, got $($tx.status) for $id"
}
function Expect-Http([string]$path, [int]$status, [string]$method = 'Get', [string]$baseUrl = $coordUrl) {
    try { Invoke-WebRequest -Method $method -Uri "$baseUrl$path" -UseBasicParsing -TimeoutSec 10 | Out-Null }
    catch {
        if ($null -ne $_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq $status) {
            Write-Host "PASS: $method $path => HTTP $status" -ForegroundColor Green
            return
        }
        throw
    }
    throw "Expected HTTP $status for $path"
}
function Start-TestService([string]$folder, [string]$assembly, [int]$port, [string]$label) {
    $directory = Join-Path $testRoot $folder
    $arguments = @(('"' + (Join-Path $directory $assembly) + '"'), '--urls', "http://localhost:$port", '--contentRoot', ('"' + $directory + '"'), '--Participants:Airline', "$airlineUrl/", '--Participants:Hotel', "$hotelUrl/")
    $process = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $arguments -WorkingDirectory $directory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testRoot "$label.stdout.log") -RedirectStandardError (Join-Path $testRoot "$label.stderr.log")
    $processes.Add($process)
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw "$label exited; see $testRoot" }
        try {
            $path = if ($folder -eq 'participant') { '/dev/vote/YES' } else { '/health' }
            $method = if ($folder -eq 'participant') { 'Post' } else { 'Get' }
            Invoke-RestMethod -Method $method -Uri "http://localhost:$port$path" -TimeoutSec 2 | Out-Null
            return $process
        } catch {}
        Start-Sleep -Milliseconds 250
    }
    throw "$label did not become ready; see $testRoot"
}

try {
    foreach ($port in @($BasePort, ($BasePort + 1), ($BasePort + 2), ($BasePort + 3))) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
        try { $listener.Start() } finally { $listener.Stop() }
    }
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    Write-Output "Test data and logs: $testRoot"
    foreach ($project in @('Travel.Coordinator', 'Travel.Participant.Mock')) {
        $folder = if ($project -eq 'Travel.Coordinator') { 'coordinator' } else { 'participant' }
        dotnet publish (Join-Path $projectRoot "$project/$project.csproj") -c Release --no-restore --nologo -o (Join-Path $testRoot $folder)
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    }
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    # Keep sandbox/limited Windows Event Log permissions out of protocol tests.
    $env:Logging__EventLog__LogLevel__Default = 'None'
    $airline = Start-TestService 'participant' 'Travel.Participant.Mock.dll' ($BasePort + 1) 'airline'
    $hotel = Start-TestService 'participant' 'Travel.Participant.Mock.dll' ($BasePort + 2) 'hotel'
    $coordinator = Start-TestService 'coordinator' 'Travel.Coordinator.dll' $BasePort 'coordinator'
    $page = Invoke-WebRequest -Uri "$coordUrl/" -UseBasicParsing
    Assert-Check ($page.Content.Contains('Student 1')) 'Coordinator diagnostic page is available'
    Expect-Http '/dev/demo/participants/Hotel/network/OFFLINE' 404 'Post'
    & (Join-Path $PSScriptRoot 'Smoke-Test.ps1') -CoordinatorUrl $coordUrl -HotelUrl $hotelUrl -AirlineUrl $airlineUrl

    $tx = Post '/api/transactions'
    $aborted = Post "/api/transactions/$($tx.transactionId)/abort"
    Assert-Check ($aborted.status -eq 'ABORTED') 'ABORT_TRANSACTION before a COMMIT decision'
    $tx = Post '/api/transactions'
    $first = Post "/api/transactions/$($tx.transactionId)/end"
    $second = Post "/api/transactions/$($tx.transactionId)/end"
    Assert-Check ($first.status -eq 'COMMITTED' -and $second.status -eq 'COMMITTED') 'Repeated END preserves the decision'
    Expect-Http "/api/transactions/$($tx.transactionId)/abort" 409 'Post'

    Invoke-RestMethod -Method Post -Uri "$hotelUrl/dev/prepare-delay/3000" | Out-Null
    $tx = Post '/api/transactions'
    $result = Post "/api/transactions/$($tx.transactionId)/end"
    $logs = Invoke-RestMethod -Uri "$coordUrl/api/transactions/$($tx.transactionId)/logs"
    Assert-Check ($result.status -eq 'ABORTED' -and @($logs | Where-Object { $_.details -like '*NO_RESPONSE*' }).Count -eq 1) 'A real HTTP timeout in PREPARE produces durable ABORT'
    Invoke-RestMethod -Method Post -Uri "$hotelUrl/dev/prepare-delay/0" | Out-Null

    Post '/dev/notifications/PAUSE' | Out-Null
    $commit = Post '/api/transactions'
    $decision = Post "/api/transactions/$($commit.transactionId)/end"
    Assert-Check ($decision.status -eq 'COMMIT_DECIDED' -and !$decision.hotelAck -and !$decision.airlineAck) 'Decision is on disk before Phase 2 notifications'
    $abort = Post '/api/transactions'
    Post "/api/transactions/$($abort.transactionId)/abort" | Out-Null
    $active = Post '/api/transactions'
    $expired = Post '/api/transactions'
    Post "/dev/transactions/$($expired.transactionId)/expire" | Out-Null

    # Start real Phase 1, observe the Airline YES vote, then kill only Coordinator.
    Invoke-RestMethod -Method Post -Uri "$hotelUrl/dev/prepare-delay/10000" | Out-Null
    $preparing = Post '/api/transactions'
    $pendingEnd = Start-Job -ArgumentList "$coordUrl/api/transactions/$($preparing.transactionId)/end" -ScriptBlock {
        param($url)
        Invoke-RestMethod -Method Post -Uri $url -TimeoutSec 15
    }
    $sawPrepared = $false
    $deadline = (Get-Date).AddSeconds(8)
    do {
        try {
            $participant = Invoke-RestMethod -Uri "$airlineUrl/api/2pc/transactions/$($preparing.transactionId)"
            $current = Read-Tx $preparing.transactionId
            if ($participant.status -eq 'PREPARED' -and $current.status -eq 'PREPARING') { $sawPrepared = $true; break }
        } catch {}
        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)
    Assert-Check $sawPrepared 'Phase 1 reached a real participant before the crash'
    Stop-Process -Id $coordinator.Id -Force
    $coordinator.WaitForExit()
    Invoke-RestMethod -Method Post -Uri "$hotelUrl/dev/prepare-delay/0" | Out-Null
    $coordinator = Start-TestService 'coordinator' 'Travel.Coordinator.dll' $BasePort 'coordinator-restarted'
    foreach ($item in @(@($commit, 'COMMITTED'), @($abort, 'ABORTED'), @($expired, 'ABORTED'), @($preparing, 'ABORTED'))) {
        $finished = Wait-State $item[0].transactionId $item[1]
        Assert-Check ($finished.airlineAck -and $finished.hotelAck) "Restart recovers $($item[1]) with both ACKs"
    }
    Assert-Check ((Read-Tx $active.transactionId).status -eq 'ACTIVE') 'Restart preserves an unexpired ACTIVE transaction'
    $logs = Invoke-RestMethod -Uri "$coordUrl/api/transactions/$($preparing.transactionId)/logs"
    Assert-Check ('DECISION_ABORT_STARTUP' -in $logs.eventType) 'Abandoned real PREPARING has a startup ABORT log'
    $logs = Invoke-RestMethod -Uri "$coordUrl/api/transactions/$($commit.transactionId)/logs"
    Assert-Check (@($logs | Where-Object { $_.eventType -like 'DECISION_ABORT*' }).Count -eq 0) 'Recovery never changes a durable COMMIT into ABORT'
    Post "/api/transactions/$($active.transactionId)/abort" | Out-Null
    Stop-Process -Id $coordinator.Id -Force
    $coordinator.WaitForExit()

    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $production = Start-TestService 'coordinator' 'Travel.Coordinator.dll' ($BasePort + 3) 'production'
    $productionUrl = 'http://localhost:' + ($BasePort + 3)
    foreach ($path in @('/', '/demo/', '/demo/app.js', '/dev/demo/state')) { Expect-Http $path 404 'Get' $productionUrl }
    Write-Host 'ALL STUDENT 1 COORDINATOR TESTS PASSED.' -ForegroundColor Green
}
finally {
    if ($null -ne $pendingEnd) { Stop-Job $pendingEnd -ErrorAction SilentlyContinue; Remove-Job $pendingEnd -Force -ErrorAction SilentlyContinue }
    foreach ($process in $processes) { if (!$process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } }
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
}
