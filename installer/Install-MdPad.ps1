<#
.SYNOPSIS
    Installs MdPad for the current user. No administrator rights required.

.DESCRIPTION
    Copies the app to %LOCALAPPDATA%\Programs\MdPad, adds Start menu (and
    optionally desktop) shortcuts, registers MdPad in Windows' "Open with" list
    for Markdown files, and creates an Add/Remove Programs entry.

    MdPad is deliberately NOT made the default handler for .md — it is offered
    as a choice in "Open with". Use -SetAsDefault to also register it as the
    per-user default, or pick it once in Explorer with "Always use this app".

.PARAMETER SourcePath
    Folder holding the published build. Defaults to the payload next to this script.

.PARAMETER SetAsDefault
    Also register MdPad as the user's default app for the file types below.

.PARAMETER NoDesktopShortcut
    Skip the desktop shortcut.

.EXAMPLE
    .\Install-MdPad.ps1
#>
[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot 'payload'),
    [switch]$SetAsDefault,
    [switch]$NoDesktopShortcut
)

$ErrorActionPreference = 'Stop'

$AppName      = 'MdPad'
$ProgId       = 'MdPad.Markdown'
$Publisher    = 'MdPad'
$Version      = '1.0.0'
$Extensions   = @('.md', '.markdown', '.mdown', '.mkd')
$InstallDir   = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"

if (-not (Test-Path $SourcePath)) {
    throw "Payload not found at '$SourcePath'. Run installer\Build-Installer.ps1 first."
}
if (-not (Test-Path (Join-Path $SourcePath 'MdPad.exe'))) {
    throw "'$SourcePath' does not contain MdPad.exe."
}

Write-Host "Installing $AppName $Version to $InstallDir" -ForegroundColor Cyan

# --- Stop a running copy so files can be replaced -----------------------------
Get-Process -Name 'MdPad' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  closing running $AppName (pid $($_.Id))"
    $_.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 700
    if (-not $_.HasExited) { $_ | Stop-Process -Force }
}

# --- Copy files ---------------------------------------------------------------
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallDir -Recurse -Force
$exe = Join-Path $InstallDir 'MdPad.exe'
$icon = Join-Path $InstallDir 'Assets\AppIcon.ico'
if (-not (Test-Path $icon)) { $icon = $exe }
Write-Host "  copied $((Get-ChildItem $InstallDir -Recurse -File).Count) files"

# --- Shortcuts ----------------------------------------------------------------
function New-Shortcut {
    param([string]$Path, [string]$Target, [string]$IconPath)
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.WorkingDirectory = Split-Path $Target -Parent
    $link.IconLocation = "$IconPath,0"
    $link.Description = 'Markdown editor and skill authoring tool'
    $link.Save()
}

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
New-Shortcut -Path (Join-Path $startMenu "$AppName.lnk") -Target $exe -IconPath $icon
Write-Host "  start menu shortcut created"

if (-not $NoDesktopShortcut) {
    New-Shortcut -Path (Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk") -Target $exe -IconPath $icon
    Write-Host "  desktop shortcut created"
}

# --- File type registration ---------------------------------------------------
# The ProgId describes how to open a document; putting it under each extension's
# OpenWithProgids adds MdPad to "Open with" without taking over the default.
$classes = 'HKCU:\Software\Classes'

New-Item -Path "$classes\$ProgId\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$classes\$ProgId" -Name '(default)' -Value 'Markdown Document'
Set-ItemProperty -Path "$classes\$ProgId" -Name 'FriendlyTypeName' -Value 'Markdown Document'
New-Item -Path "$classes\$ProgId\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$classes\$ProgId\DefaultIcon" -Name '(default)' -Value "`"$icon`",0"
Set-ItemProperty -Path "$classes\$ProgId\shell\open\command" -Name '(default)' -Value "`"$exe`" `"%1`""

# "Open with MdPad" for any file type, and the app's entry in the Open with list.
$appKey = "$classes\Applications\MdPad.exe"
New-Item -Path "$appKey\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path $appKey -Name 'FriendlyAppName' -Value $AppName
New-Item -Path "$appKey\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$appKey\DefaultIcon" -Name '(default)' -Value "`"$icon`",0"
Set-ItemProperty -Path "$appKey\shell\open\command" -Name '(default)' -Value "`"$exe`" `"%1`""
New-Item -Path "$appKey\SupportedTypes" -Force | Out-Null
foreach ($ext in $Extensions + @('.txt')) {
    Set-ItemProperty -Path "$appKey\SupportedTypes" -Name $ext -Value ''
}

foreach ($ext in $Extensions) {
    New-Item -Path "$classes\$ext\OpenWithProgids" -Force | Out-Null
    Set-ItemProperty -Path "$classes\$ext\OpenWithProgids" -Name $ProgId -Value ([byte[]]@()) -Type Binary

    if ($SetAsDefault) {
        Set-ItemProperty -Path "$classes\$ext" -Name '(default)' -Value $ProgId
    }
}
Write-Host "  registered for $($Extensions -join ', ')$(if ($SetAsDefault) { ' (as default)' } else { ' (Open with)' })"

# Capabilities, so MdPad shows up in Settings > Default apps.
$capKey = "HKCU:\Software\$Publisher\$AppName\Capabilities"
New-Item -Path "$capKey\FileAssociations" -Force | Out-Null
Set-ItemProperty -Path $capKey -Name 'ApplicationName' -Value $AppName
Set-ItemProperty -Path $capKey -Name 'ApplicationDescription' -Value 'Markdown editor and skill authoring tool'
foreach ($ext in $Extensions) {
    Set-ItemProperty -Path "$capKey\FileAssociations" -Name $ext -Value $ProgId
}
New-Item -Path 'HKCU:\Software\RegisteredApplications' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\RegisteredApplications' -Name $AppName -Value "Software\$Publisher\$AppName\Capabilities"

# --- Add/Remove Programs ------------------------------------------------------
$uninstaller = Join-Path $InstallDir 'Uninstall-MdPad.ps1'
New-Item -Path $UninstallKey -Force | Out-Null
Set-ItemProperty -Path $UninstallKey -Name 'DisplayName' -Value $AppName
Set-ItemProperty -Path $UninstallKey -Name 'DisplayVersion' -Value $Version
Set-ItemProperty -Path $UninstallKey -Name 'Publisher' -Value $Publisher
Set-ItemProperty -Path $UninstallKey -Name 'DisplayIcon' -Value $icon
Set-ItemProperty -Path $UninstallKey -Name 'InstallLocation' -Value $InstallDir
Set-ItemProperty -Path $UninstallKey -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty -Path $UninstallKey -Name 'NoRepair' -Value 1 -Type DWord
Set-ItemProperty -Path $UninstallKey -Name 'EstimatedSize' -Value ([int]((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)) -Type DWord
Set-ItemProperty -Path $UninstallKey -Name 'UninstallString' `
    -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstaller`""

# --- Tell the shell to pick up the new associations ---------------------------
Add-Type -Namespace Win32 -Name Shell -MemberDefinition @'
[DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
'@
[Win32.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)  # SHCNE_ASSOCCHANGED

Write-Host ""
Write-Host "$AppName installed." -ForegroundColor Green
Write-Host "  Right-click any .md file > Open with > $AppName"
Write-Host "  Uninstall from Settings > Apps, or run $uninstaller"
