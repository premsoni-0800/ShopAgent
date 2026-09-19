@echo off
REM Rebuild the .NET agent and launch it. Use this after editing any C# file.
cd /d "%~dp0NET"
echo Building...
dotnet build .\src\PrintlyAgent\PrintlyAgent.csproj -c Debug -v minimal
if errorlevel 1 (
  echo.
  echo BUILD FAILED - not launching. Scroll up for the error.
  pause
  exit /b 1
)
echo.
echo Starting Printly Partner...
start "" "%~dp0NET\src\PrintlyAgent\bin\Debug\net8.0-windows\win-x64\PrintlyAgentNet.exe"
