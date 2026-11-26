# Pull, build and launch LocalBloops for a live iRacing test.
# Usage (from the repo root):  powershell -ExecutionPolicy Bypass -File scripts\run-live-test.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

# 1. Latest code
git pull

# 2. .NET 8 SDK
$hasSdk = $false
try { $hasSdk = (& dotnet --list-sdks 2>$null) -match '^8\.' } catch {}
if (-not $hasSdk) {
    Write-Host 'Installing .NET 8 SDK...'
    winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
    $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User')
}

# 3. Build
dotnet build -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# 4. Make sure iRacing is not in exclusive fullscreen (the overlay can't draw over it)
$ini = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'iRacing\rendererDX11Monitor.ini'
if (Test-Path $ini) {
    $text = Get-Content $ini -Raw
    $fixed = $text -replace '(?m)^(\s*fullScreen\s*=\s*)1', '${1}0' -replace '(?m)^(\s*border\s*=\s*)1', '${1}0'
    if ($fixed -ne $text) {
        if (Get-Process -Name 'iRacingSim64DX11' -ErrorAction SilentlyContinue) {
            Write-Warning "iRacing is running and is set to exclusive fullscreen. Close it and run this script again."
        } else {
            Copy-Item $ini "$ini.bak" -Force
            Set-Content $ini $fixed -NoNewline
            Write-Host "Set fullScreen=0 and border=0 in $ini (backup: $ini.bak)"
        }
    } else {
        Write-Host 'iRacing renderer already windowed/borderless.'
    }
} else {
    Write-Host "No $ini yet (start iRacing once, then rerun if needed)."
}

# 5. Launch in the background; it appears as a tray icon
Start-Process dotnet -ArgumentList 'run', '-c', 'Release', '--no-build', '--project', 'src\LocalBloops' -WindowStyle Hidden
Write-Host ''
Write-Host 'LocalBloops is running (tray icon). Do not press Alt+Enter in the sim.'
Write-Host 'Tray -> load a Garage 61 CSV / .ibt (or use your PB), then drive.'
Write-Host 'Ctrl+Shift+L = unlock/drag/lock, Ctrl+Shift+H = hide/show.'
Write-Host "PBs save to $env:LOCALAPPDATA\LocalBloops\laps\"
