<#
.SYNOPSIS
    Builds a signed MSIX package for MdPad.

.DESCRIPTION
    Drives the Windows App SDK single-project MSIX tooling through `dotnet build`
    to produce a self-contained .msix (no .NET runtime or Windows App SDK needed on
    the target), then signs it so Windows will install it.

    Signing:
      * With -CertificatePath, signs with your real code-signing certificate (.pfx).
        A package signed by a publicly-trusted cert installs by double-click with no
        extra steps.
      * Otherwise, a self-signed developer certificate whose subject matches the
        manifest's Publisher is created (once) in Cert:\CurrentUser\My and reused.
        Its public half is exported next to the package as MdPad.cer, and end users
        trust it via Install-Msix.ps1 before installing.

.EXAMPLE
    .\Build-Msix.ps1
    .\Build-Msix.ps1 -Runtime win-arm64
    .\Build-Msix.ps1 -CertificatePath C:\certs\mdpad.pfx -CertificatePassword (Read-Host -AsSecureString)
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'win-x86')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$Version = '1.2.0',
    [string]$CertificatePath,
    [System.Security.SecureString]$CertificatePassword
)

$ErrorActionPreference = 'Stop'

$root     = Split-Path $PSScriptRoot -Parent
$project  = Join-Path $root 'MdPad.csproj'
$manifest = Join-Path $root 'Package.appxmanifest'
$platform = switch ($Runtime) { 'win-arm64' { 'ARM64' } 'win-x86' { 'x86' } default { 'x64' } }

# --- Locate signtool.exe from the Windows SDK BuildTools NuGet package ---------
$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$signtool = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools\*\bin\*\$hostArch\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found. Run 'dotnet restore' first so the Windows SDK BuildTools package is present." }

# --- Build the MSIX (dotnet drives the single-project MSIX targets) ------------
$buildDir = Join-Path $root "artifacts\msix\$platform"
Write-Host "Building MdPad MSIX ($Configuration / $Runtime)" -ForegroundColor Cyan
if (Test-Path $buildDir) { Remove-Item $buildDir -Recurse -Force }
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null

& dotnet build $project `
    -c $Configuration `
    -p:Platform=$platform `
    -p:RuntimeIdentifier=$Runtime `
    -p:WindowsPackageType=MSIX `
    -p:WindowsAppSDKSelfContained=true `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=false `
    -p:AppxBundle=Never `
    -p:UapAppxPackageBuildMode=SideloadOnly `
    -p:AppxPackageDir="$buildDir\"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$msix = Get-ChildItem $buildDir -Recurse -Filter '*.msix' | Sort-Object Length -Descending | Select-Object -First 1
if (-not $msix) { throw "No .msix was produced under $buildDir" }
Write-Host "  package: $($msix.Name) ($([math]::Round($msix.Length / 1MB, 1)) MB)"

# --- Sign ---------------------------------------------------------------------
$cer = $null
if ($CertificatePath) {
    Write-Host "Signing with certificate $CertificatePath" -ForegroundColor Cyan
    $args = @('sign', '/fd', 'SHA256', '/f', $CertificatePath)
    if ($CertificatePassword) {
        $args += @('/p', [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertificatePassword)))
    }
    $args += $msix.FullName
    & $signtool.FullName @args
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
}
else {
    # Match the Publisher in the manifest exactly - a signature only counts if its
    # subject equals Identity/@Publisher.
    [xml]$mx = Get-Content $manifest
    $publisher = $mx.Package.Identity.Publisher
    Write-Host "Signing with self-signed developer certificate ($publisher)" -ForegroundColor Cyan

    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher } | Select-Object -First 1
    if (-not $cert) {
        Write-Host "  creating a new self-signed certificate"
        $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher `
            -KeyUsage DigitalSignature -FriendlyName 'MdPad Dev Signing' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    & $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint $msix.FullName
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }

    # Export the public certificate so end users can trust it before installing.
    $cer = Join-Path $buildDir 'MdPad.cer'
    Export-Certificate -Cert $cert -FilePath $cer -Type CERT | Out-Null
}

# --- Assemble a clean, distributable folder -----------------------------------
$outName = "MdPad-$Version-$Runtime-msix"
$outDir  = Join-Path $root "artifacts\$outName"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

Copy-Item $msix.FullName $outDir
if ($cer) { Copy-Item $cer $outDir }
Copy-Item (Join-Path $PSScriptRoot 'Install-Msix.ps1') $outDir -ErrorAction SilentlyContinue

$zip = Join-Path $root "artifacts\$outName.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip

Write-Host ""
Write-Host "MSIX package: $(Join-Path $outDir $msix.Name)" -ForegroundColor Green
Write-Host "Zipped:       $zip" -ForegroundColor Green
if ($cer) {
    Write-Host ""
    Write-Host "This build is self-signed. To install, either:" -ForegroundColor Yellow
    Write-Host "  * run Install-Msix.ps1 (imports the certificate, then installs), or"
    Write-Host "  * import MdPad.cer into 'Trusted People' (LocalMachine), then double-click the .msix"
}
