@echo off
setlocal
cd /d "%~dp0"
taskkill /im CotLModManager.exe /f >nul 2>&1
dotnet publish CotLModManager.csproj -c Release -o "%~dp0bin\publish" --nologo -v q
if errorlevel 1 exit /b 1
copy /y "%~dp0bin\publish\CotLModManager.exe" "%~dp0CotLModManager.exe" >nul
echo Built CotLModManager.exe
