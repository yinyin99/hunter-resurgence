@echo off
rem Usage: publish.cmd <path to your settings.json> [version, e.g. 1.2.3 or 1.2.3-rc.1]
rem Publishes the single-file exe, copies the given settings.json and the three .cmd files next to it, then zips them for distribution.
if "%~1"=="" (echo usage: publish.cmd ^<path to settings.json^> [version] & exit /b 1)
if not exist "%~1" (echo not found: %~1 & exit /b 1)
set OUT=%~dp0bin\Release\net10.0-windows\win-x64
set VER=
if not "%~2"=="" set VER=-p:Version=%~2
dotnet publish "%~dp0play.csproj" -p:PublishProfile=win-x64 %VER% || exit /b
copy /y "%~1" "%OUT%\publish\settings.json" >nul || exit /b
copy /y "%~dp0cmd\*.cmd" "%OUT%\publish\" >nul || exit /b
del "%OUT%\play-win-x64.zip" 2>nul
tar -a -c -f "%OUT%\play-win-x64.zip" -C "%OUT%\publish" play.exe settings.json 1-setup.cmd 2-run.cmd 3-remove.cmd || exit /b
echo exe, settings.json and the .cmd files in %OUT%\publish, release zip %OUT%\play-win-x64.zip
