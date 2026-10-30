@echo off
setlocal
cd /d "%~dp0"

rem Close a running instance first, otherwise the build cannot overwrite the exe.
taskkill /IM MiLauncher.exe /F >nul 2>&1

echo [1/3] Building MiLauncher...
dotnet build "MiLauncher.csproj" --nologo -v quiet
if errorlevel 1 goto :fail

echo [2/3] Starting...
rem Runs as a normal user (app.manifest uses asInvoker); no elevation needed.
start "" "%~dp0bin\Debug\net8.0-windows\MiLauncher.exe"

echo [3/3] Checking...
rem The console returning to the prompt is normal: the launcher runs in its own window.
rem Full path on purpose: launched from Git Bash, PATH contains unix tools that
rem would shadow the Windows ones.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "Start-Sleep -Seconds 3; if (Get-Process -Name MiLauncher -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"
if errorlevel 1 goto :notrunning

echo [OK] Launcher is running. This console can be closed.
goto :eof

:notrunning
echo [FAIL] MiLauncher.exe is not running.
echo.
echo Error log: %APPDATA%\MiLauncher\error.log
echo Asset log: %APPDATA%\MiLauncher\assets.log
pause
exit /b 1

:fail
echo.
echo Build failed. See messages above.
pause
exit /b 1
