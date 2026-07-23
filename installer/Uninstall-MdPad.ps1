<#
.SYNOPSIS
    Removes MdPad and everything Install-MdPad.ps1 registered for the current user.
#>
[CmdletBinding()]
param([switch]$KeepFiles)

$ErrorActionPreference = 'Continue'

$AppName    = 'MdPad'
$ProgId     = 'MdPad.Markdown'
$Publisher  = 'MdPad'
$Extensions = @('.md', '.markdown', '.mdown', '.mkd')
$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$classes    = 'HKCU:\Software\Classes'

Write-Host "Uninstalling $AppName" -ForegroundColor Cyan

Get-Process -Name 'MdPad' -ErrorAction SilentlyContinue | ForEach-Object {
    $_.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 700
    if (-not $_.HasExited) { $_ | Stop-Process -Force }
}

# Shortcuts
$shortcuts = @(
    (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk")
)
foreach ($path in $shortcuts) {
    if (Test-Path $path) { Remove-Item $path -Force; Write-Host "  removed $path" }
}

# File type registration
foreach ($ext in $Extensions) {
    $openWith = "$classes\$ext\OpenWithProgids"
    if (Test-Path $openWith) {
        Remove-ItemProperty -Path $openWith -Name $ProgId -ErrorAction SilentlyContinue
    }
    # Only clear the default if it is ours; never touch another app's association.
    $current = (Get-ItemProperty -Path "$classes\$ext" -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
    if ($current -eq $ProgId) {
        Set-ItemProperty -Path "$classes\$ext" -Name '(default)' -Value ''
    }
}

foreach ($key in @("$classes\$ProgId", "$classes\Applications\MdPad.exe", "HKCU:\Software\$Publisher")) {
    if (Test-Path $key) { Remove-Item $key -Recurse -Force; Write-Host "  removed $key" }
}
Remove-ItemProperty -Path 'HKCU:\Software\RegisteredApplications' -Name $AppName -ErrorAction SilentlyContinue

# Add/Remove Programs entry
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"
if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse -Force }

# Files — deleted last, and from a copy of the script if it lives in the install dir.
if (-not $KeepFiles -and (Test-Path $InstallDir)) {
    if ($PSCommandPath -and $PSCommandPath.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase)) {
        $temp = Join-Path $env:TEMP "MdPad-uninstall-$([guid]::NewGuid().ToString('N')).ps1"
        Copy-Item $PSCommandPath $temp -Force
        Start-Process powershell.exe -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
            '-File', "`"$temp`"", '-KeepFiles:$false'
        ) -WindowStyle Hidden
        Write-Host "  file removal handed to a temporary copy of the uninstaller"
        return
    }

    Start-Sleep -Milliseconds 500
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $InstallDir) {
        Write-Warning "Could not delete $InstallDir - remove it manually."
    } else {
        Write-Host "  removed $InstallDir"
    }
}

Add-Type -Namespace Win32 -Name Shell -MemberDefinition @'
[DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
'@
[Win32.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host "$AppName removed." -ForegroundColor Green
