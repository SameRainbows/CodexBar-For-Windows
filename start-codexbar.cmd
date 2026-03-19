@echo off
setlocal

set "ROOT=%~dp0"
set "APP_DEBUG=%ROOT%src\CodexBar.App\bin\Debug\net8.0-windows\CodexBar.App.exe"
set "APP_RELEASE=%ROOT%src\CodexBar.App\bin\Release\net8.0-windows\CodexBar.App.exe"
set "APP="

if exist "%APP_DEBUG%" set "APP=%APP_DEBUG%"
if not defined APP if exist "%APP_RELEASE%" set "APP=%APP_RELEASE%"

if not defined APP (
    echo Building CodexBar...
    dotnet build "%ROOT%CodexBar.sln" -c Debug >nul
    if errorlevel 1 (
        echo Build failed. Run "dotnet build CodexBar.sln" for details.
        exit /b 1
    )
    if exist "%APP_DEBUG%" set "APP=%APP_DEBUG%"
)

if not defined APP (
    echo Could not locate CodexBar.App.exe after build.
    exit /b 1
)

start "" "%APP%" --show
exit /b 0
