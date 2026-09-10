@echo off
setlocal
cd /d "%~dp0"
dotnet publish src\Kazrich.App\Kazrich.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist\RichType
if errorlevel 1 exit /b 1
echo.
echo Ready: dist\RichType\RichType.exe
