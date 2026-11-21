@echo off
rem ============================================================
rem  LiteRec build script
rem  Uses only the built-in .NET Framework compiler.
rem  No installation required. ASCII-only on purpose:
rem  cmd.exe mis-parses UTF-8 line continuations.
rem ============================================================
setlocal

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. Please install .NET Framework 4.x.
    if not defined CI pause
    exit /b 1
)

set "DIR=%~dp0"
set "SRC=%DIR%src"
set "OUT1=%DIR%LiteRec.exe"
set "OUT2=%DIR%LiteRec.Test.exe"

set "COMMON=/nologo /optimize+ /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll"

rem Icon: /win32icon embeds it as the exe's shell icon (Explorer, taskbar, Alt-Tab).
rem The app reads it back from its own PE resources at runtime, so no second copy.
set "ICON=%DIR%assets\literec.ico"
set "ICONOPTS="
if exist "%ICON%" set "ICONOPTS=/win32icon:%ICON%"

echo [1/3] Building LiteRec.exe ...
"%CSC%" %COMMON% %ICONOPTS% /target:winexe /out:"%OUT1%" "%SRC%\AssemblyInfo.cs" "%SRC%\Program.cs" "%SRC%\MainForm.cs" "%SRC%\Settings.cs" "%SRC%\Interop.cs" "%SRC%\MfNative.cs" "%SRC%\AudioCapture.cs" "%SRC%\GlobalHotkey.cs" "%SRC%\ScreenCapture.cs" "%SRC%\CursorImage.cs" "%SRC%\AviWriter.cs" "%SRC%\Mp4Writer.cs" "%SRC%\Recorder.cs" "%SRC%\RegionSelector.cs" "%SRC%\ControlBar.cs"
if errorlevel 1 goto fail

echo [2/3] Building LiteRec.Test.exe ...
"%CSC%" %COMMON% /target:exe /out:"%OUT2%" "%SRC%\TestMain.cs" "%SRC%\Interop.cs" "%SRC%\MfNative.cs" "%SRC%\AudioCapture.cs" "%SRC%\ScreenCapture.cs" "%SRC%\CursorImage.cs" "%SRC%\AviWriter.cs" "%SRC%\Mp4Writer.cs" "%SRC%\Recorder.cs"
if errorlevel 1 goto fail

echo [3/3] Done.
echo.
echo   Main app : %OUT1%
for %%A in ("%OUT1%") do echo   Size     : %%~zA bytes
echo   Self test: %OUT2%
echo.
echo Double-click LiteRec.exe to run. No install, no dependencies.
echo Self test usage:
echo   LiteRec.Test.exe mp4 3         - record 3s to MP4
echo   LiteRec.Test.exe avi 3         - record 3s to AVI
echo   LiteRec.Test.exe mp4 3 full    - record 3s fullscreen
echo.
rem Skip the interactive pause in CI (GitHub Actions sets CI=true)
if not defined CI pause
exit /b 0

:fail
echo.
echo [ERROR] Build failed. See messages above.
if not defined CI pause
exit /b 1
