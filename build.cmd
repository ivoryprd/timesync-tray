@echo off
REM Builds TimeSync.exe using the C# compiler that ships with Windows.
REM No SDK, no NuGet, no toolchain install - csc.exe is already on every
REM machine with .NET Framework 4.x, which is all of them.
REM
REM Note: that compiler only supports C# 5, so the source deliberately avoids
REM string interpolation, null-conditional operators and expression-bodied
REM members. Keep it that way or the build breaks.

setlocal

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo Could not find csc.exe - is .NET Framework 4.x installed?
    exit /b 1
)

"%CSC%" /nologo /optimize+ /platform:anycpu /target:winexe ^
    /out:TimeSync.exe ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Drawing.dll ^
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
