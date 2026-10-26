# Builds Usage Bar for Windows, runs its tests, installs it for this user and starts it.
# Usage: powershell -ExecutionPolicy Bypass -File windows\scripts\install.ps1 [-SkipTests]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'

$windows = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\UsageBar'
$stage = Join-Path ([IO.Path]::GetTempPath()) ("UsageBar-publish-" + [guid]::NewGuid().ToString('N'))

Write-Host "Checking the .NET SDK..."
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Usage Bar needs the .NET 10 SDK, and 'dotnet' isn't on PATH: https://dotnet.microsoft.com/download/dotnet/10.0"
}
$sdk = (& dotnet --list-sdks) | Where-Object { $_ -match '^10\.' }
if (-not $sdk) { throw "Usage Bar needs the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0" }
Write-Host "  ok   .NET SDK $(($sdk | Select-Object -Last 1).Split(' ')[0])"

if (-not $SkipTests) {
    Write-Host "Running the tests..."
    & dotnet test (Join-Path $windows 'tests\UsageBar.Tests') --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; nothing was installed." }
    Write-Host "  ok   tests passed"
}

# Build first, into a staging folder: a failed build leaves the running copy untouched.
Write-Host "Building..."
& dotnet publish (Join-Path $windows 'src\UsageBar') -c Release -o $stage --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
    throw "Build failed; the installed copy was left as it was."
}
Write-Host "  ok   built"

try {
    # Only the installed copy: a demo or development build running from elsewhere is left alone.
    Write-Host "Stopping the installed Usage Bar..."
    $installedExe = Join-Path $dest 'UsageBar.exe'
    Get-Process UsageBar -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -ieq [IO.Path]::GetFullPath($installedExe)) } |
        ForEach-Object { $_ | Stop-Process -Force; $_.WaitForExit(5000) | Out-Null }

    # A clean folder, so files from an older version don't linger.
    Write-Host "Installing to $dest..."
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item -Recurse -Force (Join-Path $stage '*') $dest
    Write-Host "  ok   installed"
}
finally {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}

Write-Host "Starting Usage Bar..."
Start-Process $installedExe
Write-Host ""
Write-Host "Done. New tray icons start in the overflow (^): drag them onto the taskbar to keep them in view."
