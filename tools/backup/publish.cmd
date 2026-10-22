@echo off
rem Usage: publish.cmd <path to your settings.json>
rem Publishes the single-file exe, copies the given settings.json next to it, then zips both for distribution.
if "%~1"=="" (echo usage: publish.cmd ^<path to settings.json^> & exit /b 1)
if not exist "%~1" (echo not found: %~1 & exit /b 1)
set OUT=%~dp0bin\Release\net10.0-windows\win-x64
dotnet publish "%~dp0backup.csproj" -p:PublishProfile=win-x64 || exit /b
copy /y "%~1" "%OUT%\publish\settings.json" >nul || exit /b
del "%OUT%\backup-win-x64.zip" 2>nul
tar -a -c -f "%OUT%\backup-win-x64.zip" -C "%OUT%\publish" backup.exe settings.json || exit /b
echo exe and settings.json in %OUT%\publish, release zip %OUT%\backup-win-x64.zip
