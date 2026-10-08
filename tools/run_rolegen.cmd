@echo off
rem Builds and runs tools\RoleGen under 32-bit Mono (developer tool; see the comment at the top of RoleGen.cs).
rem Usage: tools\run_rolegen.cmd <local role map> ["<game install dir>"] [check]
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set MONO=%ProgramFiles(x86)%\Mono\bin\mono.exe
cd /d "%~dp0.."
set OUT=local\rolegen
if not exist "%OUT%" mkdir "%OUT%"
"%CSC%" /nologo /langversion:5 /platform:x86 /out:"%OUT%\RoleGen.exe" /r:System.Core.dll tools\RoleGen\RoleGen.cs server\src\Resolve\Fingerprint.cs
if errorlevel 1 exit /b 1
"%MONO%" "%OUT%\RoleGen.exe" %1 %2 %3
