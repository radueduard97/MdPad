@echo off
rem Double-click entry point: runs the PowerShell installer without needing to
rem change the machine's execution policy.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-MdPad.ps1" %*
echo.
pause
