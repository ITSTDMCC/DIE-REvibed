@echo off
rem Runs the local server under 32-bit Mono with the match client's own game
rem logic as the match server (--game). The game's libraries and data are read
rem from the owner's install at run time; nothing is copied or changed there.
rem Logs go to the repo's logs\ folder. Run server\build.cmd first.
rem Usage: tools\run_server_mono.cmd ["<game install dir>"]
setlocal
set GAME=%~1
if "%GAME%"=="" set GAME=C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic
set MONO=%ProgramFiles(x86)%\Mono\bin\mono.exe
if not exist "%MONO%" (
  echo 32-bit Mono is needed: the game's libraries only run under Mono.
  exit /b 1
)
cd /d "%~dp0.."
rem The server creates logs\ here (the repo root) before it switches to the install folder.
"%MONO%" server\bin\EpidemicServer.exe "--game=%GAME%"
