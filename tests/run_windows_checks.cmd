@echo off
rem Checks the server's encodings against the game's own serializers: the story
rem map blob and weapons (uniques), both loaded by reflection from the owner's
rem install at run time. Builds into local\ (git-ignored).
rem Usage: tests\run_windows_checks.cmd ["<game install dir>"]
setlocal
set GAME=%~1
if "%GAME%"=="" set GAME=C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic
set MANAGED=%GAME%\Dead Island Epidemic_Data\Managed
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0.."
set OUT=local\windows-checks
if not exist "%OUT%" mkdir "%OUT%"
"%CSC%" /nologo /langversion:5 /platform:x86 /nowarn:1684 /out:"%OUT%\WindowsChecks.exe" ^
  /r:System.Core.dll /recurse:server\src\*.cs tests\StoryMapCheck.cs tests\InventoryCheck.cs /main:StoryMapCheck
if errorlevel 1 exit /b 1
rem The game's libraries only run under 32-bit Mono.
set MONO=%ProgramFiles(x86)%\Mono\bin\mono.exe
if exist "%MONO%" (
  "%MONO%" "%OUT%\WindowsChecks.exe" "%MANAGED%"
) else (
  "%OUT%\WindowsChecks.exe" "%MANAGED%"
)
