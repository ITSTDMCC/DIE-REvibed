@echo off
rem Builds and runs tools\ScoutProbe under 32-bit Mono against the owner's install (read-only).
rem Usage: tools\run_scout_probe.cmd [map index, 15 Outpost / 16 Lab / 17 Club] ["<game install dir>"]
setlocal
set MAP=%~1
if "%MAP%"=="" set MAP=15
set GAME=%~2
if "%GAME%"=="" set GAME=C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set MONO=%ProgramFiles(x86)%\Mono\bin\mono.exe
cd /d "%~dp0.."
set OUT=local\scout-probe
if not exist "%OUT%" mkdir "%OUT%"
"%CSC%" /nologo /langversion:5 /platform:x86 /out:"%OUT%\ScoutProbe.exe" /r:System.Core.dll /r:System.Drawing.dll /main:ScoutProbe tools\ScoutProbe\ScoutProbe.cs /recurse:server\src\*.cs
if errorlevel 1 exit /b 1
"%MONO%" "%OUT%\ScoutProbe.exe" "%GAME%" %MAP%
