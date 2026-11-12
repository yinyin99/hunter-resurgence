@echo off
rem Usage: publish.cmd <path to your settings.json> [version, e.g. 1.2.3 or 1.2.3-rc.1]
rem Publishes the self-contained launcher, copies the given settings.json next to the exe, then packs it with Velopack (the dotnet tool vpk, the version Velopack in the csproj has: dotnet tool install -g vpk --version <it>)
rem into %OUT%\releases: Setup.exe, the full package, a delta package when releases of an older version are already in that folder, and releases.win.json.
if "%~1"=="" (echo usage: publish.cmd ^<path to settings.json^> [version] & exit /b 1)
if not exist "%~1" (echo not found: %~1 & exit /b 1)
set OUT=%~dp0bin\Release\net10.0-windows10.0.17763.0\win-x64
set VER=
set PACKVER=0.0.1-dev
if not "%~2"=="" set VER=-p:Version=%~2
if not "%~2"=="" set PACKVER=%~2
dotnet publish "%~dp0Resurgence.Launcher.csproj" -p:PublishProfile=win-x64 %VER% || exit /b
copy /y "%~1" "%OUT%\publish\settings.json" >nul || exit /b
vpk pack --packId HunterResurgence --packTitle "Hunter Resurgence" --packVersion %PACKVER% --runtime win-x64 --packDir "%OUT%\publish" --mainExe resurgence-launcher.exe --icon "%~dp0wwwroot\favicon.ico" --outputDir "%OUT%\releases" || exit /b
echo exe, settings.json and wwwroot in %OUT%\publish, Velopack release in %OUT%\releases
