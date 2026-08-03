@echo off
setlocal EnableExtensions

if /I not "%~1"=="__RUN_UNINSTALL" (
    start "RemoteController Agent Uninstall" cmd.exe /d /k call "%~f0" __RUN_UNINSTALL
    exit /b 0
)

set "POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%POWERSHELL%" set "POWERSHELL=powershell.exe"
set "RC_UNINSTALLER=%~dp0Uninstall-RemoteController.ps1"
set "RC_UNINSTALL_POWERSHELL=%POWERSHELL%"
set "RC_ONE_CLICK_UNINSTALL=1"

"%POWERSHELL%" -NoProfile -ExecutionPolicy Bypass -File "%RC_UNINSTALLER%"
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" (
    echo.
    echo [ERROR] RemoteController Agent uninstall failed with exit code %EXIT_CODE%.
    echo Review the PowerShell error output above for the failure reason.
) else (
    echo.
    echo [OK] RemoteController Agent was fully uninstalled.
)
echo.
pause
exit /b %EXIT_CODE%
