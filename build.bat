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
rem Starting the program inside publish makes its own Mods, Profiles and s3mo.ini there; copying those would
rem replace the real mod list and settings.
if exist "publish\s3mo.ini" goto leftovers
if exist "publish\Profiles" goto leftovers
if exist "publish\Mods" goto leftovers
echo Copy everything in that folder into your S3MO folder (the one with Mods, Profiles and s3mo.ini).
pause
exit /b 0

:leftovers
echo.
echo Careful: the publish folder also has Mods, Profiles or s3mo.ini from starting the program there.
echo Copy only the program files into your S3MO folder: s3mo.exe and the .dll, .json and .pdb files.
echo Copying the rest would replace your real mod list and settings.
pause
