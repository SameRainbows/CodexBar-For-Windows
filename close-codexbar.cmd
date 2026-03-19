@echo off
setlocal

set "ROOT=%~dp0"
set "APP_DEBUG=%ROOT%src\CodexBar.App\bin\Debug\net8.0-windows\CodexBar.App.exe"
set "APP_RELEASE=%ROOT%src\CodexBar.App\bin\Release\net8.0-windows\CodexBar.App.exe"
set "APP="

if exist "%APP_DEBUG%" set "APP=%APP_DEBUG%"
if not defined APP if exist "%APP_RELEASE%" set "APP=%APP_RELEASE%"

if defined APP (
    start "" "%APP%" --exit
    exit /b 0
)

tasklist /FI "IMAGENAME eq CodexBar.App.exe" | find /I "CodexBar.App.exe" >nul
if errorlevel 1 (
    echo CodexBar is not running.
    exit /b 0
)

echo CodexBar executable path was not found; force-closing running instance...
taskkill /IM CodexBar.App.exe /F >nul
exit /b %ERRORLEVEL%
