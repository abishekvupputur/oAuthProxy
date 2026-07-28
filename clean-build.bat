@echo off
setlocal

echo Stopping any running OAuthProxy.exe...
taskkill /IM OAuthProxy.exe /F >nul 2>&1

REM taskkill returns before Windows releases the file handles, which makes the
REM clean below fail with "Access is denied". Wait for the process to actually go.
for /l %%i in (1,1,20) do (
    tasklist /FI "IMAGENAME eq OAuthProxy.exe" 2>nul | find /i "OAuthProxy.exe" >nul || goto :stopped
    ping -n 2 127.0.0.1 >nul
)
:stopped

echo Cleaning bin/obj...
for %%P in (src\OAuthProxy.Core src\OAuthProxy.App tests\OAuthProxy.Core.Tests) do (
    if exist "%%P\bin" rmdir /s /q "%%P\bin"
    if exist "%%P\obj" rmdir /s /q "%%P\obj"
)

REM WPF markup compilation runs through a temporary *_wpftmp project. On a freshly wiped
REM obj/ it intermittently fails to hand the generated *.g.cs files to the main compile,
REM producing bogus errors ("CS2001: MainWindow.g.cs could not be found", or "CS5001: no
REM static Main"). -m:1 (no parallel MSBuild) makes it much rarer but does NOT eliminate it;
REM the generated files exist by the second pass, so retry once before declaring failure.
echo Building...
dotnet build OAuthProxy.slnx -c Debug -m:1
if errorlevel 1 (
    echo First build pass failed - retrying once ^(WPF markup-compile quirk^)...
    dotnet build OAuthProxy.slnx -c Debug -m:1
    if errorlevel 1 (
        echo Build FAILED.
        exit /b 1
    )
)

echo Build succeeded.

echo Starting OAuthProxy...
start "" "%~dp0src\OAuthProxy.App\bin\Debug\net8.0-windows\OAuthProxy.exe"

echo Done - app running in tray.
endlocal
