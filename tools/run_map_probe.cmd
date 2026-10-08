@echo off
rem Builds and runs tools\MapProbe under 32-bit Mono against the owner's install (read-only).
rem Usage: tools\run_map_probe.cmd <map index> <game mode type> ["<game install dir>"]
rem   Horde: maps 5, 6, 7 with type 4. Scavenger: maps 1, 2, 3, 8, 14 with type 6.
setlocal
set MAP=%~1
set MODE=%~2
set GAME=%~3
if "%GAME%"=="" set GAME=C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set MONO=%ProgramFiles(x86)%\Mono\bin\mono.exe
cd /d "%~dp0.."
set OUT=local\map-probe
if not exist "%OUT%" mkdir "%OUT%"
"%CSC%" /nologo /langversion:5 /platform:x86 /out:"%OUT%\MapProbe.exe" /r:System.Core.dll /r:System.Drawing.dll /main:MapProbe tools\MapProbe\MapProbe.cs /recurse:server\src\*.cs
if errorlevel 1 exit /b 1
"%MONO%" "%OUT%\MapProbe.exe" "%GAME%" %MAP% %MODE%
