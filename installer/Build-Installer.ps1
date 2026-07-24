<#
.SYNOPSIS
    Publishes MdPad and assembles a distributable installer folder + zip.

.DESCRIPTION
    Runs dotnet publish (self-contained, so the target machine needs no .NET or
    Windows App SDK), copies the result into installer\payload, drops the install
    and uninstall scripts alongside it, and zips the lot as MdPad-<version>-win-x64.zip.

.EXAMPLE
    .\Build-Installer.ps1
    .\Build-Installer.ps1 -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$Version = '1.1.0'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'MdPad.csproj'
$payload = Join-Path $PSScriptRoot 'payload'
$publishDir = Join-Path $root "artifacts\publish\$Runtime"

Write-Host "Publishing MdPad ($Configuration / $Runtime)" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

& dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:Platform=$(if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }) `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "Assembling payload" -ForegroundColor Cyan
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

# Ship the app, minus build leftovers that the installer does not need.
Get-ChildItem $publishDir -Recurse -File |
    Where-Object { $_.Extension -notin @('.pdb', '.xml') -or $_.Name -eq 'MdPad.xml' } |
    ForEach-Object {
        $relative = $_.FullName.Substring($publishDir.Length).TrimStart('\')
        $target = Join-Path $payload $relative
        $dir = Split-Path $target -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Copy-Item $_.FullName $target -Force
    }

Copy-Item (Join-Path $PSScriptRoot 'Uninstall-MdPad.ps1') $payload -Force

$size = [math]::Round(((Get-ChildItem $payload -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "  payload: $((Get-ChildItem $payload -Recurse -File).Count) files, $size MB"

# --- Zip for distribution -----------------------------------------------------
$stage = Join-Path $env:TEMP "MdPad-stage-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item $payload (Join-Path $stage 'payload') -Recurse
Copy-Item (Join-Path $PSScriptRoot 'Install-MdPad.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'Uninstall-MdPad.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'Install.cmd') $stage -ErrorAction SilentlyContinue

$zip = Join-Path $root "artifacts\MdPad-$Version-$Runtime.zip"
$artifacts = Split-Path $zip -Parent
if (-not (Test-Path $artifacts)) { New-Item -ItemType Directory -Path $artifacts -Force | Out-Null }
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force

Write-Host ""
Write-Host "Installer package: $zip" -ForegroundColor Green
Write-Host "  install locally:  installer\Install-MdPad.ps1"
Write-Host "  or from the zip:  double-click Install.cmd"
