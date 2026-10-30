# Run this in PowerShell terminal 3. Press Ctrl+C to stop.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project './Travel.Coordinator/Travel.Coordinator.csproj' -- --urls 'http://localhost:5000'
