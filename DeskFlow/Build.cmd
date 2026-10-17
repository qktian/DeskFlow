@echo off
setlocal
pushd "%~dp0"
set "DESKFLOW_DOTNET=dotnet"
where dotnet >nul 2>nul
if not errorlevel 1 goto sdk_found
set "DESKFLOW_DOTNET=%USERPROFILE%\.workbuddy\binaries\dotnet\dotnet.exe"
if exist "%DESKFLOW_DOTNET%" goto sdk_found
echo .NET 8 SDK was not found. Install it, then run this script again.
goto failed

:sdk_found
echo [1/6] Building solution (Release)...
"%DESKFLOW_DOTNET%" build DeskFlow.sln -c Release
if errorlevel 1 goto failed
echo [2/6] Checking column calculation...
"%DESKFLOW_DOTNET%" run --project tests\DeskFlow.LayoutRegression -c Release --no-build
if errorlevel 1 goto failed
echo [3/6] Checking Windows box layout...
"%DESKFLOW_DOTNET%" run --project tests\DeskFlow.Regression -c Release --no-build -- --box-layout
if errorlevel 1 goto failed
echo [4/6] Checking todo completion effect...
"%DESKFLOW_DOTNET%" run --project tests\DeskFlow.Regression -c Release --no-build -- --todo-effect
if errorlevel 1 goto failed
echo [5/6] Checking pomodoro timer...
"%DESKFLOW_DOTNET%" run --project tests\DeskFlow.PomodoroRegression -c Release --no-build
if errorlevel 1 goto failed
echo [6/6] Publishing to publish\ ...
"%DESKFLOW_DOTNET%" publish DeskFlow.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
if errorlevel 1 goto failed
echo.
echo Done. The updated program is publish\DeskFlow.exe
echo Close the running DeskFlow before starting the updated program.
popd
exit /b 0

:failed
echo.
echo Build or verification failed. See the error above.
popd
exit /b 1
