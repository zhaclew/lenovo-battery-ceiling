@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. Please install .NET Framework 4.x first.
    pause
    exit /b 1
)

if not exist "dist" mkdir "dist"

echo Compiling src\BatteryCeiling.cs ...
"%CSC%" /nologo /target:winexe /optimize+ /out:"dist\BatteryCeiling.exe" "src\BatteryCeiling.cs"
if errorlevel 1 (
    echo [ERROR] Build failed.
    pause
    exit /b 1
)

echo.
echo Done: dist\BatteryCeiling.exe
pause