@echo off
setlocal
cd /d "%~dp0"
if exist "artifacts\DomainDesk-win-x64-v1.5.1\DomainDesk.exe" (
    start "" "artifacts\DomainDesk-win-x64-v1.5.1\DomainDesk.exe"
    exit /b 0
)
if exist "artifacts\DomainDesk-win-x64-v1.5.0\DomainDesk.exe" (
    start "" "artifacts\DomainDesk-win-x64-v1.5.0\DomainDesk.exe"
    exit /b 0
)
if exist "artifacts\DomainDesk-win-x64-v1.4.0\DomainDesk.exe" (
    start "" "artifacts\DomainDesk-win-x64-v1.4.0\DomainDesk.exe"
    exit /b 0
)
if exist "artifacts\DomainDesk-win-x64-v1.3.0\DomainDesk.exe" (
    start "" "artifacts\DomainDesk-win-x64-v1.3.0\DomainDesk.exe"
    exit /b 0
)
if exist "artifacts\DomainDesk-win-x64-v1.2.1\DomainDesk.exe" (
    start "" "artifacts\DomainDesk-win-x64-v1.2.1\DomainDesk.exe"
    exit /b 0
)
if exist "artifacts\DomainDesk-win-x64-v1.2.0\DomainDesk.exe" (
    start "" "artifacts\DomainDesk-win-x64-v1.2.0\DomainDesk.exe"
    exit /b 0
)
if exist "artifacts\DomainDesk-win-x64-v1.1.0\DomainDesk.exe" (
    start "" "artifacts\DomainDesk-win-x64-v1.1.0\DomainDesk.exe"
    exit /b 0
)
if exist "artifacts\DomainDesk-win-x64-ip-20261028\DomainDesk.exe" (
    start "" "artifacts\DomainDesk-win-x64-ip-20261028\DomainDesk.exe"
    exit /b 0
)
echo Build the desktop client first: powershell -File build.ps1 -Test -Publish
pause
exit /b 1
