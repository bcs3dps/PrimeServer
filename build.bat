@echo off
REM ==========================================================================
REM   build.bat - compile PrimeServe.exe with csc.exe (.NET Framework 4.x)
REM
REM   Headless C# RFB server over a synthetic framebuffer.  Explicit source
REM   list (build-scripts-never-glob rule): every .cs file is named here.
REM   Run from bash via:  cmd.exe //c ".\build.bat"
REM ==========================================================================
setlocal

REM -- Locate the C# 5.0 compiler (prefer 64-bit, fall back to 32-bit). --------
set "CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo build.bat: csc.exe not found under %windir%\Microsoft.NET
    exit /b 3
)

REM -- Output location. --------------------------------------------------------
if not exist "%~dp0build" mkdir "%~dp0build"
set "OUT=%~dp0build\PrimeServe.exe"

REM -- Kill a running instance so the exe is not locked. -----------------------
taskkill /f /im PrimeServe.exe >nul 2>&1

REM -- Compile.  csc.rsp supplies System.dll etc.; add it explicitly to be sure.
REM    Library sources first (PixelFormat, Des, FrameBuffer, RfbServer), then
REM    the test harness (TestScreens, Program).
"%CSC%" /nologo /target:exe /optimize+ /out:"%OUT%" ^
    /reference:System.dll ^
    "%~dp0src\PixelFormat.cs" ^
    "%~dp0src\Des.cs" ^
    "%~dp0src\FrameBuffer.cs" ^
    "%~dp0src\RfbServer.cs" ^
    "%~dp0src\TestScreens.cs" ^
    "%~dp0src\Program.cs"

if errorlevel 1 (
    echo build.bat: BUILD FAILED
    exit /b 1
)

echo build.bat: built %OUT%
exit /b 0
