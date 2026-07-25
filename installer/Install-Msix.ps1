<#
.SYNOPSIS
    Installs the MdPad MSIX package.

.DESCRIPTION
    A package signed with a self-signed developer certificate can only be installed
    once that certificate is trusted on the machine. This script imports the bundled
    MdPad.cer into the LocalMachine "Trusted People" store (which needs administrator
    rights, so it self-elevates), then installs the .msix for the current user.

    If the package was signed with a publicly-trusted certificate there is no .cer to
    import - just double-click the .msix, or run this script and it will skip the
    trust step.

.EXAMPLE
    .\Install-Msix.ps1
    .\Install-Msix.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

if ($Uninstall) {
    $pkg = Get-AppxPackage -Name '*MdPad*', 'EC2EAD67-BE5E-4C79-B8CA-BABFBE7C5062' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $pkg) {
        $pkg = Get-AppxPackage | Where-Object { $_.PublisherId -and $_.Name -eq 'EC2EAD67-BE5E-4C79-B8CA-BABFBE7C5062' } | Select-Object -First 1
    }
    if ($pkg) {
        Write-Host "Removing $($pkg.PackageFullName)" -ForegroundColor Cyan
        Remove-AppxPackage $pkg.PackageFullName
        Write-Host "MdPad uninstalled." -ForegroundColor Green
    }
    else {
        Write-Host "MdPad is not installed." -ForegroundColor Yellow
    }
    return
}

$msix = Get-ChildItem $here -Filter '*.msix' | Select-Object -First 1
if (-not $msix) { throw "No .msix found next to this script." }
$cer = Get-ChildItem $here -Filter '*.cer' | Select-Object -First 1

# --- Trust the signing certificate (self-signed builds only) ------------------
if ($cer) {
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host "Trusting the signing certificate requires administrator rights - elevating..." -ForegroundColor Yellow
        Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass',
            '-Command', "Import-Certificate -FilePath `"$($cer.FullName)`" -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null"
        )
        Write-Host "  certificate imported into LocalMachine\TrustedPeople"
    }
    else {
        Import-Certificate -FilePath $cer.FullName -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
        Write-Host "Certificate imported into LocalMachine\TrustedPeople" -ForegroundColor Cyan
    }
}

# --- Install the package ------------------------------------------------------
Write-Host "Installing $($msix.Name)" -ForegroundColor Cyan
Add-AppxPackage -Path $msix.FullName

Write-Host ""
Write-Host "MdPad installed. Find it in the Start menu." -ForegroundColor Green
Write-Host "Uninstall with: Settings -> Apps -> MdPad, or  .\Install-Msix.ps1 -Uninstall"
