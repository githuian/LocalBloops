# Publishes LocalBloops as a self-contained win-x64 app and builds the Inno Setup installer.
# Output: artifacts\LocalBloops-Setup-<version>.exe
#
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 0.1.0]
#
# The version defaults to <Version> in LocalBloops.csproj. Needs the .NET 8 SDK and Inno Setup 6.
param(
    [string]$Version,
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\LocalBloops\LocalBloops.csproj'
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'

if (-not $Version) {
    [xml]$csproj = Get-Content $project
    $Version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (-not $Version) { throw "No <Version> in $project; pass -Version." }
}
$Version = $Version.TrimStart('v')

if (-not $Iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $Iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) not found; install it or pass -Iscc.' }
}

Write-Host "Building LocalBloops $Version"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

& $Iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" "/DOutputDir=$artifacts" (Join-Path $PSScriptRoot 'LocalBloops.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

$setup = Join-Path $artifacts "LocalBloops-Setup-$Version.exe"
Write-Host "Installer: $setup"
