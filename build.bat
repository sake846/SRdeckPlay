@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Usage: build.bat [configuration] [--no-pause]
set "BUILD_EXIT_CODE=1"
set "BUILD_CONFIGURATION=Release"
if not "%~1"=="" set "BUILD_CONFIGURATION=%~1"

pushd "%~dp0"
if errorlevel 1 goto finish

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: Install the .NET 10 SDK and reopen this script.
    goto end
)

echo Restoring SRdeckPlay...
dotnet restore "SRdeckPlay.sln" -m:1 -p:Configuration=%BUILD_CONFIGURATION% -p:Platform=x64
set "BUILD_EXIT_CODE=%ERRORLEVEL%"
if not "%BUILD_EXIT_CODE%"=="0" goto end

echo Building SRdeckPlay in %BUILD_CONFIGURATION% x64...
dotnet build "SRdeckPlay.sln" --configuration "%BUILD_CONFIGURATION%" -m:1 --no-restore -p:Platform=x64
set "BUILD_EXIT_CODE=%ERRORLEVEL%"
if not "%BUILD_EXIT_CODE%"=="0" goto end

echo Build succeeded. SRdeckPlay.exe is in SRdeckPlay\bin\x64\%BUILD_CONFIGURATION%\net10.0-windows\win-x64.

:end
if not "%BUILD_EXIT_CODE%"=="0" echo Build failed. Exit code: %BUILD_EXIT_CODE%
popd

:finish
if /i not "%~2"=="--no-pause" pause
exit /b %BUILD_EXIT_CODE%
