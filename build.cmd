@echo off
REM Builds TimeSync.exe using the C# compiler that ships with Windows.
REM No SDK, no NuGet, no toolchain install - csc.exe is already on every
REM machine with .NET Framework 4.x, which is all of them.
REM
REM Note: that compiler only supports C# 5, so the source deliberately avoids
REM string interpolation, null-conditional operators and expression-bodied
REM members. Keep it that way or the build breaks.
REM
REM The tray icons are embedded from icons\tray\*.ico, which are committed to
REM the repo. If you change the artwork in RabbitArt, regenerate them first:
REM     TimeSync.exe --export-icons
REM then run this script again to embed the new versions.

setlocal

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo Could not find csc.exe - is .NET Framework 4.x installed?
    exit /b 1
)

REM Embedded resources are the tray-sized icons only (16/24/32/48). The exe's
REM own shell icon uses the full set, which carries the 128 and 256 entries
REM Explorer wants in large-icon views.
set RES=
set RES=%RES% /resource:icons\tray\rabbit-ok.ico,rabbit-ok.ico
set RES=%RES% /resource:icons\tray\rabbit-syncing.ico,rabbit-syncing.ico
set RES=%RES% /resource:icons\tray\rabbit-warning.ico,rabbit-warning.ico
set RES=%RES% /resource:icons\tray\rabbit-error.ico,rabbit-error.ico

if not exist "icons\tray\rabbit-ok.ico" (
    echo icons\tray\*.ico are missing - building without embedded icons,
    echo then regenerating them. Run build.cmd again afterwards.
    "%CSC%" /nologo /optimize+ /platform:anycpu /target:winexe ^
        /out:TimeSync.exe ^
        /reference:System.Windows.Forms.dll ^
        /reference:System.Drawing.dll ^
        TimeSync.cs
    if errorlevel 1 exit /b 1
    TimeSync.exe --export-icons
    echo Icons regenerated. Re-run build.cmd to embed them.
    exit /b 0
)

"%CSC%" /nologo /optimize+ /platform:anycpu /target:winexe ^
    /out:TimeSync.exe ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Drawing.dll ^
    /win32icon:icons\rabbit-ok.ico ^
    %RES% ^
    TimeSync.cs

if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

echo Built TimeSync.exe
echo.
echo   TimeSync.exe                  run the tray app
echo   TimeSync.exe --check          headless one-shot, writes to timesync.log
echo   TimeSync.exe --show           start with the debug window open
echo   TimeSync.exe --export-icons   regenerate the icon set into icons\
