@echo off
setlocal
set "SurroundTouchPowerShell=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "SurroundTouchPowerShell=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%SurroundTouchPowerShell%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup.ps1" -Mode Install
if errorlevel 1 pause
endlocal
