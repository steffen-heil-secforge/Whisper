@echo off
setlocal enabledelayedexpansion

REM ========================================================================
REM  build.bat — Copies the repo to %SystemDrive%\Whisper-Compiling and
REM              invokes compile.bat there.
REM ========================================================================

set "SOURCE_DIR=%~dp0"
if "%SOURCE_DIR:~-1%"=="\" set "SOURCE_DIR=%SOURCE_DIR:~0,-1%"

set "BUILD_DIR=%SystemDrive%\Whisper-Compiling"

echo.
echo  Source : %SOURCE_DIR%
echo  Build  : %BUILD_DIR%
echo.

REM --- Erase previous build folder if it exists -----------------------------
if exist "%BUILD_DIR%" (
    echo Removing previous build folder...
    rd /s /q "%BUILD_DIR%"
    if exist "%BUILD_DIR%" (
        echo ERROR: Failed to remove "%BUILD_DIR%". Close any programs using it.
        exit /b 1
    )
)

REM --- Copy source to build folder ------------------------------------------
echo Copying source to build folder...
robocopy "%SOURCE_DIR%" "%BUILD_DIR%" /E /XD .git .vs .claude bin obj x64 packages node_modules /XF *.user *.suo >nul
if %ERRORLEVEL% GEQ 8 (
    echo ERROR: robocopy failed with exit code %ERRORLEVEL%.
    exit /b 1
)

if not exist "%BUILD_DIR%\compile.bat" (
    echo ERROR: Copy failed — compile.bat not found in build folder.
    exit /b 1
)
echo Done.
echo.

REM --- Hand off to compile.bat in the build folder --------------------------
call "%BUILD_DIR%\compile.bat"
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: compile.bat failed with exit code !ERRORLEVEL!.
    exit /b !ERRORLEVEL!
)

REM --- Copy dist back to source folder --------------------------------------
set "DIST_DIR=%SOURCE_DIR%\dist"
echo.
echo Copying dist to source folder...
if exist "%DIST_DIR%" rd /s /q "%DIST_DIR%"
mkdir "%DIST_DIR%"

robocopy "%BUILD_DIR%\dist" "%DIST_DIR%" /E >nul
if !ERRORLEVEL! GEQ 8 (
    echo ERROR: Failed to copy dist back to source folder.
    exit /b 1
)

echo Done.
echo.
echo ========================================================================
echo  dist: %DIST_DIR%
echo ========================================================================
