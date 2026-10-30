@echo off
setlocal
cd /d "%~dp0"

rem Builds a self-contained single-file exe under .\publish and writes SHA256SUMS.txt.
rem Copy that folder to any Windows 10/11 x64 machine - no .NET runtime needed.
rem
rem Code signing is opt-in: drop a tools\signing.local.json in place and this
rem script will sign automatically. See 签名与分发说明.md.

echo [1/4] Publishing MiLauncher...
if exist "publish" rd /s /q "publish"
dotnet publish "MiLauncher.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -o "publish"
if errorlevel 1 goto :fail

echo.
echo [2/4] Signing...
if not exist "tools\signing.local.json" goto :nosign
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\sign.ps1" -Path "publish\MiLauncher.exe"
if errorlevel 1 echo   signing reported a problem, see messages above
goto :signed

:nosign
echo   skipped - no tools\signing.local.json

:signed
echo.
echo [3/4] Checksum...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$h=(Get-FileHash 'publish\MiLauncher.exe' -Algorithm SHA256).Hash.ToLower(); $h + '  MiLauncher.exe' | Set-Content -Encoding ASCII 'publish\SHA256SUMS.txt'; '  ' + $h"

echo.
echo [4/4] Result:
for %%F in ("publish\MiLauncher.exe") do echo   %%~fF   %%~zF bytes

echo.
echo Done. Copy the publish folder to the target machine and run MiLauncher.exe.
pause
goto :eof

:fail
echo.
echo Publish failed. See messages above.
pause
exit /b 1
