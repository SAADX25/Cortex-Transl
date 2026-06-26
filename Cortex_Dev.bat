@echo off
setlocal

cd /d "%~dp0"

set "PROJECT=src\CortexTransl.App\CortexTransl.App.csproj"
set "DOTNET="

if exist "%~dp0..dotnet-sdk\dotnet.exe" (
    set "DOTNET=%~dp0..dotnet-sdk\dotnet.exe"
) else if exist "%~dp0.dotnet-sdk\dotnet.exe" (
    set "DOTNET=%~dp0.dotnet-sdk\dotnet.exe"
) else (
    set "DOTNET=dotnet"
)

echo Cortex Transl developer launcher
echo Using .NET: %DOTNET%
echo.

if not exist "%PROJECT%" (
    echo Project file not found: %PROJECT%
    goto :error
)

echo Restoring...
"%DOTNET%" restore "%PROJECT%"
if errorlevel 1 goto :error

echo.
echo Building Debug...
"%DOTNET%" build "%PROJECT%" --configuration Debug --no-restore
if errorlevel 1 goto :error

echo.
echo Starting Cortex Transl...
"%DOTNET%" run --project "%PROJECT%" --configuration Debug --no-build
if errorlevel 1 goto :error

echo.
echo Cortex Transl closed.
exit /b 0

:error
set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" set "EXIT_CODE=1"
echo.
echo Cortex Transl developer launcher failed. Exit code: %EXIT_CODE%
echo Press any key to close this window.
pause >nul
exit /b %EXIT_CODE%
