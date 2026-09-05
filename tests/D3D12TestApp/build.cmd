@echo off
setlocal
rem Builds D3D12TestApp.exe into tests\D3D12TestApp\bin using the VS 2022 x64 toolchain.
set HERE=%~dp0
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VSDIR=%%i
if "%VSDIR%"=="" (
  echo Visual Studio with C++ tools not found.
  exit /b 1
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul
if not exist "%HERE%bin" mkdir "%HERE%bin"
cl /nologo /EHsc /O2 /W3 /std:c++17 /DUNICODE /D_UNICODE /Fo"%HERE%bin\\" /Fe"%HERE%bin\D3D12TestApp.exe" "%HERE%main.cpp" /link /SUBSYSTEM:CONSOLE
if errorlevel 1 exit /b 1
echo Built %HERE%bin\D3D12TestApp.exe
