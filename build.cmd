@echo off
rem Builds build-release\WCWMayhem_Recompiled.exe with the patched psxrecomp in psxrecomp\ (run
rem scripts\setup-psxrecomp.ps1 first). The first build needs your own disc:
rem   build.cmd "D:\Games\WCW Mayhem (Europe).cue"
rem Later builds need no arguments. See scripts\build.ps1 for -Regenerate.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1" %*
exit /b %errorlevel%
