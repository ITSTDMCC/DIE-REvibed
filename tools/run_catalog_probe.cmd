@echo off
rem Builds and runs tools\CatalogProbe under 32-bit Mono against the owner's install (read-only).
rem Usage: tools\run_catalog_probe.cmd ["<game install dir>"]
rem   Horde: maps 5, 6, 7 with type 4. Scavenger: maps 1, 2, 3, 8, 14 with type 6.
setlocal
set GAME=%~1
if "%GAME%"=="" set GAME=C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set MONO=%ProgramFiles(x86)%\Mono\bin\mono.exe
cd /d "%~dp0.."
set OUT=local\catalog-probe
if not exist "%OUT%" mkdir "%OUT%"
"%CSC%" /nologo /langversion:5 /platform:x86 /out:"%OUT%\CatalogProbe.exe" /r:System.Core.dll /r:System.Drawing.dll /main:CatalogProbe tools\CatalogProbe\CatalogProbe.cs /recurse:server\src\*.cs
if errorlevel 1 exit /b 1
"%MONO%" "%OUT%\CatalogProbe.exe" "%GAME%"
