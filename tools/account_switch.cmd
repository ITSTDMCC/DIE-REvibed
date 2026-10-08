@echo off
rem Turns the local account's preservation switches on or off (server\bin\account.txt).
rem Usage: tools\account_switch.cmd <unlockAll|maxLevel|unlimitedCurrency|all> <on|off>
rem   unlockAll          every character, weapon, gadget, design, part and consumable, and every game mode
rem   maxLevel           the account level shown and used in matches is the last level that unlocks something
rem   unlimitedCurrency  gold, silver, character and research points shown as 9,999,999
rem The real progress in the file is never changed. The server picks the change up at the next hub login
rem (log in again, or restart the hub).
setlocal
set KEY=%~1
set VALUE=%~2
if /i "%VALUE%"=="on" (set VALUE=true) else if /i "%VALUE%"=="off" (set VALUE=false) else goto usage
if /i "%KEY%"=="all" (
  call "%~f0" unlockAll %2 && call "%~f0" maxLevel %2 && call "%~f0" unlimitedCurrency %2
  exit /b %errorlevel%
)
if /i not "%KEY%"=="unlockAll" if /i not "%KEY%"=="maxLevel" if /i not "%KEY%"=="unlimitedCurrency" goto usage
set FILE=%~dp0..\server\bin\account.txt
if not exist "%FILE%" (
  echo No account file yet at %FILE%; start the server and log in once first.
  exit /b 1
)
powershell -NoProfile -Command ^
  "$f = '%FILE%'; $k = '%KEY%'; $v = '%VALUE%';" ^
  "$lines = @(Get-Content -LiteralPath $f | Where-Object { $_ -notmatch ('^' + $k + '=') });" ^
  "$lines += ($k + '=' + $v);" ^
  "Set-Content -LiteralPath $f -Value $lines -Encoding ASCII"
if errorlevel 1 exit /b 1
echo %KEY%=%VALUE% (takes effect at the next hub login)
exit /b 0
:usage
echo Usage: tools\account_switch.cmd ^<unlockAll^|maxLevel^|unlimitedCurrency^|all^> ^<on^|off^>
exit /b 2
