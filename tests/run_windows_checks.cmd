@echo off
rem Checks the server's encodings against the game's own serializers:
rem the story map blob against the match client's ConductorCrafting.dll, and
rem weapons (uniques) against Assembly-CSharp.dll (MessageSerialization).
rem Reads the game's libraries from the owner's install; builds into local\ (git-ignored).
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
  /r:"%MANAGED%\ConductorCrafting.dll" /r:"%MANAGED%\StunCore.dll" /r:System.Core.dll ^
  /recurse:server\src\*.cs tests\StoryMapCheck.cs tests\InventoryCheck.cs /main:StoryMapCheck
if errorlevel 1 exit /b 1
rem The story map check loads the match client's libraries from beside it, so copy
rem them into the git-ignored build folder (the install itself is never modified).
copy /y "%MANAGED%\ConductorCrafting.dll" "%OUT%\" >nul
copy /y "%MANAGED%\StunCore.dll" "%OUT%\" >nul
rem The weapon checks call into Assembly-CSharp.dll, which only runs under Mono
rem (32-bit); without it those cases are reported as skipped.
set MONO=%ProgramFiles(x86)%\Mono\bin\mono.exe
if exist "%MONO%" (
  "%MONO%" "%OUT%\WindowsChecks.exe" "%MANAGED%"
) else (
  "%OUT%\WindowsChecks.exe" "%MANAGED%"
)
