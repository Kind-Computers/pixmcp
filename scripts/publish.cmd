@echo off
rem Publishes PixMcp.exe as a single framework-dependent file into dist\ (needs the .NET 10 runtime
rem on the target machine; the PIX API is loaded from the PIX Preview install at runtime, so it is
rem never bundled). Point your MCP client at dist\PixMcp.exe.
setlocal
cd /d "%~dp0.."
dotnet publish src\PixMcp\PixMcp.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist %*
if errorlevel 1 exit /b %errorlevel%
echo.
echo Published to %CD%\dist\PixMcp.exe
echo Register it with:  claude mcp add pix -- "%CD%\dist\PixMcp.exe"
