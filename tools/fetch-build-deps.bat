@echo off
setlocal
rem =====================================================================
rem  Restore the offline build toolchain (no Visual Studio, no admin).
rem
rem  Downloads into tools\build-deps:
rem    - Roslyn 4.8 compiler (Microsoft.Net.Compilers.Toolset 4.8.0)
rem    - .NET Framework 4.8 reference assemblies (official nuget package)
rem
rem  Needs: internet + Python 3 on PATH.
rem =====================================================================

set "SCRIPT=%~dp0fetch_build_deps.py"

where python >nul 2>&1
if errorlevel 1 goto :no_python

python "%SCRIPT%"
if errorlevel 1 goto :failed

echo.
exit /b 0

:no_python
echo [ERROR] python not found on PATH.
echo.
echo   Install Python 3 (https://www.python.org/downloads/) or, alternatively,
echo   install Visual Studio 2022 Build Tools with the ".NET desktop build
echo   tools" component, then just run build-release.bat directly.
echo.
exit /b 1

:failed
echo [ERROR] Dependency download failed. Check your network / proxy settings.
exit /b 1
