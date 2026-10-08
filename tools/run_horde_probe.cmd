@echo off
rem Builds and runs tools\HordeProbe under 32-bit Mono against the owner's install (read-only).
rem Usage: tools\run_horde_probe.cmd [map index, 5 Outpost / 6 Lab / 7 Club] ["<game install dir>"]
setlocal
set MAP=%~1
if "%MAP%"=="" set MAP=5
set GAME=%~2
if "%GAME%"=="" set GAME=C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set MONO=%ProgramFiles(x86)%\Mono\bin\mono.exe
cd /d "%~dp0.."
set OUT=local\horde-probe
if not exist "%OUT%" mkdir "%OUT%"
"%CSC%" /nologo /langversion:5 /platform:x86 /out:"%OUT%\HordeProbe.exe" /r:System.Core.dll /r:System.Drawing.dll /main:HordeProbe tools\HordeProbe\HordeProbe.cs /recurse:server\src\*.cs
if errorlevel 1 exit /b 1
"%MONO%" "%OUT%\HordeProbe.exe" "%GAME%" %MAP% %3
