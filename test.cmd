@echo off
setlocal
cd /d "%~dp0"
dotnet run --project tests\Kazrich.Tests\Kazrich.Tests.csproj -c Release
exit /b %errorlevel%
