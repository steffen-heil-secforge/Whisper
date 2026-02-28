@echo off
setlocal enabledelayedexpansion

REM ========================================================================
REM  compile.bat — Builds the full solution (Release|x64) from the
REM                directory where this script resides.
REM
REM  Uses MSBuild (VS Build Tools) for C++ projects and
REM  dotnet CLI for C# SDK-style projects.
REM ========================================================================

set "BUILD_DIR=%~dp0"
if "%BUILD_DIR:~-1%"=="\" set "BUILD_DIR=%BUILD_DIR:~0,-1%"

set "CONFIG=Release"
set "PLATFORM=x64"

echo.
echo  Build  : %BUILD_DIR%
echo  Config : %CONFIG%^|%PLATFORM%
echo.

REM --- Find Visual Studio / MSBuild via vswhere -----------------------------
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo ERROR: vswhere not found at "%VSWHERE%"
    echo        Install Visual Studio 2022+ with the C++ desktop workload.
    exit /b 1
)

set "MSBUILD="
set "VS_PATH="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -property installationPath 2^>nul`) do (
    set "VS_PATH=%%i"
)
if defined VS_PATH (
    if exist "!VS_PATH!\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD=!VS_PATH!\MSBuild\Current\Bin\MSBuild.exe"
    )
)
if not defined MSBUILD (
    echo ERROR: MSBuild not found.
    echo        Install Visual Studio 2022+ with C++ desktop workload.
    exit /b 1
)
echo  MSBuild: %MSBUILD%

REM --- Verify dotnet CLI is available ----------------------------------------
where dotnet >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: dotnet CLI not found. Install the .NET SDK.
    exit /b 1
)

REM --- Detect installed C++ platform toolset ---------------------------------
set "TOOLSET="
set "VC_TARGETS="
for /d %%d in ("!VS_PATH!\MSBuild\Microsoft\VC\v*") do set "VC_TARGETS=%%d"
if defined VC_TARGETS (
    for /d %%t in ("!VC_TARGETS!\Platforms\x64\PlatformToolsets\v*") do set "TOOLSET=%%~nxt"
)

if defined TOOLSET (
    echo  Toolset: !TOOLSET!
    set "CPP_PROPS=-p:PlatformToolset=!TOOLSET!"
) else (
    echo  Toolset: default ^(auto^)
    set "CPP_PROPS="
)
echo.

REM --- NuGet / .NET restore -------------------------------------------------
echo Restoring packages...
dotnet restore "%BUILD_DIR%\WhisperCpp.sln" -p:Platform=%PLATFORM% --verbosity minimal
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Package restore failed.
    exit /b 1
)
echo.

REM --- Step 1: Build ComputeShaders — HLSL -> .cso (C++, MSBuild) -----------
echo [1/5] Building ComputeShaders...
"%MSBUILD%" "%BUILD_DIR%\ComputeShaders\ComputeShaders.vcxproj" -p:Configuration=%CONFIG% -p:Platform=%PLATFORM% -p:SolutionDir="%BUILD_DIR%\\" %CPP_PROPS% -verbosity:minimal -nologo
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: ComputeShaders build failed.
    exit /b 1
)
echo.

REM --- Step 2: Build CompressShaders tool (C#, dotnet) -----------------------
echo [2/5] Building CompressShaders tool...
dotnet build "%BUILD_DIR%\Tools\CompressShaders\CompressShaders.csproj" -c %CONFIG% --no-restore --verbosity minimal
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: CompressShaders build failed.
    exit /b 1
)
echo.

REM --- Step 3: Run CompressShaders (generates shaderData-*.inl) --------------
echo [3/5] Running CompressShaders...
dotnet run --project "%BUILD_DIR%\Tools\CompressShaders\CompressShaders.csproj" -c %CONFIG% --no-build
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: CompressShaders failed.
    exit /b 1
)
echo.

REM --- Step 4: Build Whisper.dll (C++, MSBuild) ------------------------------
echo [4/5] Building Whisper.dll...
"%MSBUILD%" "%BUILD_DIR%\Whisper\Whisper.vcxproj" -p:Configuration=%CONFIG% -p:Platform=%PLATFORM% -p:SolutionDir="%BUILD_DIR%\\" %CPP_PROPS% -verbosity:minimal -nologo
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Whisper.dll build failed.
    exit /b 1
)
echo.

REM --- Step 5: Build WhisperTray (C#, dotnet) --------------------------------
echo [5/5] Building WhisperTray...
dotnet build "%BUILD_DIR%\Examples\WhisperTray\WhisperTray.csproj" -c %CONFIG% -p:GeneratePackageOnBuild=false --no-restore --verbosity minimal
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: WhisperTray build failed.
    exit /b 1
)
echo.

REM --- Assemble dist folder --------------------------------------------------
echo Assembling dist folder...
set "DIST_DIR=%BUILD_DIR%\dist"
set "APP_OUT=%BUILD_DIR%\Examples\WhisperTray\bin\%PLATFORM%\%CONFIG%"

if exist "%DIST_DIR%" rd /s /q "%DIST_DIR%"
mkdir "%DIST_DIR%"

robocopy "%APP_OUT%" "%DIST_DIR%" /E >nul
if %ERRORLEVEL% GEQ 8 (
    echo ERROR: Failed to copy WhisperTray output to dist folder.
    exit /b 1
)

if not exist "%DIST_DIR%\WhisperTray.exe" (
    echo ERROR: WhisperTray.exe not found in dist folder.
    exit /b 1
)
if not exist "%DIST_DIR%\Whisper.dll" (
    echo ERROR: Whisper.dll not found in dist folder.
    exit /b 1
)
echo Done.
echo.

REM --- Done ------------------------------------------------------------------
echo ========================================================================
echo  BUILD SUCCEEDED
echo  dist: %DIST_DIR%
echo ========================================================================
exit /b 0
