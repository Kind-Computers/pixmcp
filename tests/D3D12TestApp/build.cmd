@echo off
setlocal
rem Builds D3D12TestApp.exe into tests\D3D12TestApp\bin using the VS 2022 x64 toolchain.
set HERE=%~dp0
rem Pinned test-only event runtime; packages remain under the ignored obj directory.
dotnet restore "%HERE%WinPixEventRuntime.restore.proj" --nologo
if errorlevel 1 exit /b 1
set PIXEVENTS=%HERE%obj\packages\winpixeventruntime\1.0.240308001\
if not exist "%PIXEVENTS%Include\WinPixEventRuntime\pix3.h" (
  echo WinPixEventRuntime headers were not restored.
  exit /b 1
)
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VSDIR=%%i
if "%VSDIR%"=="" (
  echo Visual Studio with C++ tools not found.
  exit /b 1
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul
if not exist "%HERE%bin" mkdir "%HERE%bin"
cl /nologo /EHsc /O2 /Zi /W3 /std:c++17 /DUNICODE /D_UNICODE /DUSE_PIX /I"%PIXEVENTS%Include\WinPixEventRuntime" /Fo"%HERE%bin\\" /Fd"%HERE%bin\D3D12TestApp.compile.pdb" /Fe"%HERE%bin\D3D12TestApp.exe" "%HERE%main.cpp" /link /SUBSYSTEM:CONSOLE /DEBUG /PDB:"%HERE%bin\D3D12TestApp.pdb" /LIBPATH:"%PIXEVENTS%bin\x64" WinPixEventRuntime.lib
if errorlevel 1 exit /b 1
copy /y "%PIXEVENTS%bin\x64\WinPixEventRuntime.dll" "%HERE%bin\WinPixEventRuntime.dll" >nul
if errorlevel 1 exit /b 1
echo Built %HERE%bin\D3D12TestApp.exe
