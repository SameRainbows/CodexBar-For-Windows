@echo off
echo Publishing CodexBar for Windows...
dotnet publish src/CodexBar.App/CodexBar.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
echo.
echo Publish complete! Executable is in the ./publish folder.
pause
