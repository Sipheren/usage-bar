# Removes Usage Bar for this user: stops it, removes launch at login and the installed files.
# Settings and state in %APPDATA%\UsageBar and the cost cache and logs in %LOCALAPPDATA%\UsageBar stay unless -Purge.
# Usage: powershell -ExecutionPolicy Bypass -File windows\scripts\uninstall.ps1 [-Purge]
param([switch]$Purge)
$ErrorActionPreference = 'Stop'

Write-Host "Stopping Usage Bar..."
Get-Process UsageBar -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Removing launch at login..."
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'UsageBar' -ErrorAction SilentlyContinue

$dest = Join-Path $env:LOCALAPPDATA 'Programs\UsageBar'
if (Test-Path $dest) {
    Write-Host "Removing $dest..."
    Start-Sleep -Milliseconds 500
    Remove-Item -Recurse -Force $dest
}

if ($Purge) {
    foreach ($dir in (Join-Path $env:APPDATA 'UsageBar'), (Join-Path $env:LOCALAPPDATA 'UsageBar')) {
        if (Test-Path $dir) { Write-Host "Removing $dir..."; Remove-Item -Recurse -Force $dir }
    }
}
Write-Host "Done. Usage Bar never changed Claude Code's or the Claude app's files, so there is nothing else to undo."
