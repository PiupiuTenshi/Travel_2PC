# Run this in PowerShell terminal 2. Press Ctrl+C to stop.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project './Travel.Participant.Mock/Travel.Participant.Mock.csproj' -- --urls 'http://localhost:5102'
