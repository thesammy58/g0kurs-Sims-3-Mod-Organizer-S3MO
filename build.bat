@echo off
rem Builds Sims 3 Mod Organizer into the "publish" folder next to this file.
rem Needs the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET 10 SDK isn't installed. Get it from https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)

echo Building...
dotnet publish "s3mo\s3mo\s3mo.csproj" -c Release -o publish
if errorlevel 1 (
    echo.
    echo Build failed - see the messages above.
    pause
    exit /b 1
)

echo.
echo Done. The program is in: %~dp0publish
echo Copy everything in that folder into your S3MO folder (the one with Mods, Profiles and s3mo.ini).
pause
