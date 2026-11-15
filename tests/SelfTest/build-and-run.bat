@echo off
setlocal EnableExtensions
rem =====================================================================
rem  Winget Manager - SelfTest build & run (one click)
rem
rem  Output: tests\SelfTest\bin\Debug\SelfTest.exe, then runs it.
rem  Exit code: 0 = all assertions passed, 1 = failures, 2 = no toolchain.
rem
rem  Two toolchains, tried in order:
rem    1) Visual Studio MSBuild (2017/2019/2022) via vswhere
rem    2) Offline toolchain: framework MSBuild 4.0 + Roslyn 4.8 compiler
rem       + official .NET Framework 4.8 reference assemblies
rem       (..\..\tools\build-deps, no Visual Studio required)
rem
rem  NOTE: keep this file ASCII-only. cmd.exe reads .bat with the OEM code
rem  page; a UTF-8 BOM-less Chinese comment gets misparsed and can swallow
rem  the following backslash, which breaks every path in the script.
rem
rem  Usage:   build-and-run.bat
rem =====================================================================

set "HERE=%~dp0"
set "ROOT=%HERE%..\..\"
set "PROJ=%HERE%SelfTest.csproj"
set "EXE=%HERE%bin\Debug\SelfTest.exe"
set "MSBUILD="
set "OFFLINE="
set "TVSWITCH="
set "ARCHWARN="

if not exist "%PROJ%" goto :no_proj

rem --- 1) vswhere (installed by every VS 2017+ setup) --------------------
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

if exist "%VSWHERE%" for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do if not defined MSBUILD set "MSBUILD=%%i"

rem --- 2) well-known VS install paths ------------------------------------
if not defined MSBUILD if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"

if defined MSBUILD goto :have_msbuild

rem --- 3) offline fallback: framework MSBuild + downloaded toolchain ------
set "FWMSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if not exist "%FWMSBUILD%" goto :no_msbuild
if not exist "%ROOT%tools\build-deps\roslyn\csc.exe" goto :no_deps
if not exist "%ROOT%tools\build-deps\refasm\build\.NETFramework\v4.8\mscorlib.dll" goto :no_deps

rem Same global-property trick as ..\..\build-release.bat: TargetFrameworkMoniker
rem must be explicit, otherwise MSBuild 4.0 skips GetReferenceAssemblyPaths and
rem silently falls back to the runtime mscorlib.
set "TargetFrameworkMoniker=.NETFramework,Version=v4.8"
set "TargetFrameworkRootPath=%ROOT%tools\build-deps\refasm\build\"
set "BypassFrameworkInstallChecks=true"
set "CscToolPath=%ROOT%tools\build-deps\roslyn\"
set "CscToolExe=csc.exe"
set "MSBUILD=%FWMSBUILD%"
set "OFFLINE=1"
set "TVSWITCH=/tv:4.0"
set "ARCHWARN=/p:ResolveAssemblyWarnOrErrorOnTargetArchitectureMismatch=None"

:have_msbuild
echo [1/3] MSBuild: %MSBUILD%
if defined OFFLINE echo       mode  : offline toolchain (Roslyn 4.8 / net48 references)

echo [2/3] Building Debug^|AnyCPU ...
"%MSBUILD%" "%PROJ%" %TVSWITCH% %ARCHWARN% /t:Rebuild /p:Configuration=Debug /nologo /v:minimal /m
if errorlevel 1 goto :build_failed

if not exist "%EXE%" goto :no_output

echo [3/3] Running bin\Debug\SelfTest.exe ...
echo.
"%EXE%"
set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="0" (echo [SelfTest] PASS - all assertions passed.) else (echo [SelfTest] FAIL - see the failing assertions above.)
endlocal & exit /b %RC%

:no_proj
echo [ERROR] Project file not found: %PROJ%
endlocal & exit /b 2

:no_msbuild
echo [ERROR] No usable MSBuild found.
echo.
echo   Either install one of these:
echo     - Visual Studio 2022 with the ".NET desktop development" workload
echo     - Visual Studio 2022 Build Tools + ".NET desktop build tools" component
echo.
echo   Or restore the offline toolchain (about 40 MB, no admin needed):
echo     ..\..\tools\fetch-build-deps.bat
echo.
endlocal & exit /b 2

:no_deps
echo [ERROR] Offline toolchain is incomplete.
echo.
echo   Expected both of these to exist:
echo     tools\build-deps\roslyn\csc.exe
echo     tools\build-deps\refasm\build\.NETFramework\v4.8\mscorlib.dll
echo.
echo   Run:  ..\..\tools\fetch-build-deps.bat
echo.
endlocal & exit /b 2

:build_failed
echo.
echo [ERROR] Build failed. See the MSBuild output above.
endlocal & exit /b 1

:no_output
echo [ERROR] Build reported success but %EXE% is missing.
echo         Check OutputPath / AssemblyName in SelfTest.csproj.
endlocal & exit /b 1
