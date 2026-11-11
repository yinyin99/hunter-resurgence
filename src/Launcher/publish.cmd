@echo off
rem Usage: publish.cmd <path to your settings.json> [version, e.g. 1.2.3 or 1.2.3-rc.1]
rem Publishes the self-contained launcher, copies the given settings.json next to the exe, then zips the exe, settings.json and wwwroot (the page the exe shows) for distribution.
if "%~1"=="" (echo usage: publish.cmd ^<path to settings.json^> [version] & exit /b 1)
if not exist "%~1" (echo not found: %~1 & exit /b 1)
set OUT=%~dp0bin\Release\net10.0-windows10.0.17763.0\win-x64
set VER=
if not "%~2"=="" set VER=-p:Version=%~2
dotnet publish "%~dp0Resurgence.Launcher.csproj" -p:PublishProfile=win-x64 %VER% || exit /b
copy /y "%~1" "%OUT%\publish\settings.json" >nul || exit /b
del "%OUT%\launcher-win-x64.zip" 2>nul
tar -a -c -f "%OUT%\launcher-win-x64.zip" -C "%OUT%\publish" resurgence-launcher.exe settings.json wwwroot || exit /b
echo exe, settings.json and wwwroot in %OUT%\publish, release zip %OUT%\launcher-win-x64.zip
