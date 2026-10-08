@echo off
rem Builds and runs tools\ScavengerProbe under 32-bit Mono against the owner's install (read-only).
rem Usage: tools\run_scavenger_probe.cmd [map index, 1 Resort / 2 Jungle / 3 Expedition] ["<game install dir>"] [mode]
setlocal
set MAP=%~1
if "%MAP%"=="" set MAP=1
set GAME=%~2
if "%GAME%"=="" set GAME=C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set MONO=%ProgramFiles(x86)%\Mono\bin\mono.exe
cd /d "%~dp0.."
set OUT=local\scavenger-probe
if not exist "%OUT%" mkdir "%OUT%"
"%CSC%" /nologo /langversion:5 /platform:x86 /out:"%OUT%\ScavengerProbe.exe" /r:System.Core.dll /r:System.Drawing.dll /main:ScavengerProbe tools\ScavengerProbe\ScavengerProbe.cs /recurse:server\src\*.cs
if errorlevel 1 exit /b 1
"%MONO%" "%OUT%\ScavengerProbe.exe" "%GAME%" %MAP% %3
