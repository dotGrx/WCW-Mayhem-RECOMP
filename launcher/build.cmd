@echo off
setlocal
rem Builds the launcher into launcher\dist\WCWMayhemLauncher.exe: one self-contained file (plus
rem dist\art\), so players need no .NET install. Needs the .NET 10 SDK.
cd /d "%~dp0"
rem A launcher that is open locks its exe, but Windows still allows renaming it: move it aside so
rem the open window keeps running, and clear copies left by earlier builds once they're closed.
del /q dist\WCWMayhemLauncher.old-*.exe 2>nul
del /q dist\WCWMayhemLauncher.exe 2>nul
if exist dist\WCWMayhemLauncher.exe move /y dist\WCWMayhemLauncher.exe dist\WCWMayhemLauncher.old-%RANDOM%.exe >nul 2>nul
dotnet publish src\WcwLauncher\WcwLauncher.csproj -c Release -o dist --nologo %* || exit /b 1
echo.
echo Built: %~dp0dist\WCWMayhemLauncher.exe
