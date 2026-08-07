@echo off
setlocal EnableExtensions

if /I "%~1"=="__RUN_SETUP" goto :run_setup

call "%~f0" __RUN_SETUP "%~1"
set "EXIT_CODE=%ERRORLEVEL%"
exit /b %EXIT_CODE%

:run_setup

set "CONFIG_PATH=%~2"
if "%CONFIG_PATH%"=="" set "CONFIG_PATH=%~dp0RemoteController.Agent.config.json"

if not exist "%CONFIG_PATH%" (
    echo [ERROR] Configuration file was not found: "%CONFIG_PATH%"
    echo        Pass a config path as the first argument or place RemoteController.Agent.config.json beside this file.
    echo.
    pause
    exit /b 2
)

set "POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%POWERSHELL%" set "POWERSHELL=powershell.exe"

"%POWERSHELL%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-RemoteControllerAgent.ps1" -ConfigPath "%CONFIG_PATH%"
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" (
    echo.
    echo [ERROR] RemoteController Agent setup failed with exit code %EXIT_CODE%.
    echo Review the PowerShell error output above for the failure reason.
) else (
    echo.
    echo [OK] RemoteController Agent setup completed successfully.
)
echo.
pause
exit /b %EXIT_CODE%
