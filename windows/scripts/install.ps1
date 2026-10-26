# Builds Usage Bar for Windows, runs its tests, installs it for this user and starts it.
# Usage: powershell -ExecutionPolicy Bypass -File windows\scripts\install.ps1 [-SkipTests]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'

$windows = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\UsageBar'

Write-Host "Checking the .NET SDK..."
$sdk = (& dotnet --list-sdks) | Where-Object { $_ -match '^10\.' }
if (-not $sdk) { throw "Usage Bar needs the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0" }
Write-Host "  ok   .NET SDK $(($sdk | Select-Object -Last 1).Split(' ')[0])"

if (-not $SkipTests) {
    Write-Host "Running the tests..."
    & dotnet test (Join-Path $windows 'tests\UsageBar.Tests') --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; nothing was installed." }
    Write-Host "  ok   tests passed"
}

Write-Host "Stopping a running Usage Bar..."
Get-Process UsageBar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Building and installing to $dest..."
& dotnet publish (Join-Path $windows 'src\UsageBar') -c Release -o $dest --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
Write-Host "  ok   installed"

Write-Host "Starting Usage Bar..."
Start-Process (Join-Path $dest 'UsageBar.exe')
Write-Host ""
Write-Host "Done. New tray icons start in the overflow (^): drag them onto the taskbar to keep them in view."
