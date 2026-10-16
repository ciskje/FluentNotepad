@echo off
REM Start FluentNotepad WinUI 3 (Debug unpackaged build)
cd /d "%~dp0"
echo Building...
dotnet build source\FluentNotepad.WinUI.csproj -c Debug
if errorlevel 1 pause & exit /b 1
echo Starting...
for /f "delims=" %%E in ('dir /b /s source\bin\Debug\FluentNotepad.exe 2^>nul') do set "EXE=%%E"
if not defined EXE pause & exit /b 1
start "" "%EXE%" %*
