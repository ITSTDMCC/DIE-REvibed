@echo off
rem Builds EpidemicServer.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find the .NET Framework 4 C# compiler.
  exit /b 1
)
cd /d "%~dp0"
if not exist bin mkdir bin
"%CSC%" /nologo /langversion:5 /out:bin\EpidemicServer.exe /recurse:src\*.cs
if errorlevel 1 exit /b 1
echo Built bin\EpidemicServer.exe
