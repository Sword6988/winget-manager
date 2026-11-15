@echo off
setlocal EnableExtensions
rem =====================================================================
rem  Winget Manager - single-file portable build
rem
rem  Output: dist\WingetManager.exe   (exactly one file, no pdb, no config)
rem
rem  Two toolchains, tried in order:
rem    1) Visual Studio MSBuild (2019/2022) if installed
rem    2) Offline toolchain: framework MSBuild 4.0 + Roslyn 4.8 compiler
rem       + official .NET Framework 4.8 reference assemblies
rem       (tools\build-deps, no Visual Studio required)
rem
rem  NOTE: keep this file ASCII-only. cmd.exe reads .bat with the OEM code
rem  page; a UTF-8 BOM-less Chinese comment gets misparsed and can swallow
rem  the following backslash, which breaks every path in the script.
rem
rem  Usage:   build-release.bat
rem =====================================================================

set "ROOT=%~dp0"
set "PROJ=%ROOT%WingetManagerWpf\WingetManagerWpf.csproj"
set "BINDIR=%ROOT%WingetManagerWpf\bin\Release"
set "DIST=%ROOT%dist"
set "MSBUILD="
set "OFFLINE="
set "TVSWITCH="

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

rem These become MSBuild global properties via the environment, which avoids
rem quoting hell for paths that contain spaces.
rem NOTE: TargetFrameworkMoniker must be set explicitly. The classic csproj
rem relies on Microsoft.Common.props to fill it in; MSBuild 4.0 does not have
rem that file, and without the moniker the GetReferenceAssemblyPaths target is
rem skipped entirely (which silently falls back to the runtime mscorlib and
rem drags mscorlib.dll + *.nlp into bin\Release).
set "TargetFrameworkMoniker=.NETFramework,Version=v4.8"
set "TargetFrameworkRootPath=%ROOT%tools\build-deps\refasm\build\"
set "BypassFrameworkInstallChecks=true"
set "CscToolPath=%ROOT%tools\build-deps\roslyn\"
set "CscToolExe=csc.exe"
set "MSBUILD=%FWMSBUILD%"
set "OFFLINE=1"
set "TVSWITCH=/tv:4.0"
rem AnyCPU output referencing an AMD64 mscorlib is expected in this mode and
rem harmless (the CLR always loads mscorlib from the runtime directory), but
rem MSB3270 would fire on every build. Silence just that one warning.
set "ARCHWARN=/p:ResolveAssemblyWarnOrErrorOnTargetArchitectureMismatch=None"

:have_msbuild

if defined OFFLINE goto :banner_offline
echo [1/4] MSBuild: %MSBUILD%
goto :build

:banner_offline
echo [1/4] MSBuild: %MSBUILD%
echo       mode  : offline toolchain (Roslyn 4.8 / net48 references)
echo       why   : no Visual Studio build tools found, using fallback

:build
echo [2/4] Building Release^|AnyCPU ...
"%MSBUILD%" "%PROJ%" %TVSWITCH% %ARCHWARN% /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /p:DebugSymbols=false /p:DebugType=None /p:GenerateSerializationAssemblies=Off /nologo /v:minimal /m
if errorlevel 1 goto :build_failed

if not exist "%BINDIR%\WingetManager.exe" goto :no_output

echo [3/4] Collecting single-file output ...
rem --- Never wipe dist\ wholesale ---------------------------------------
rem dist is not just the output folder: in portable mode the app keeps its
rem user data in dist\Data (selection.json = checkbox state, metadata-cache.json,
rem logs\). A previous version ran `rd /s /q dist` here, which silently deleted
rem all of it on every rebuild. Remove only the build artifacts instead.
if not exist "%DIST%" mkdir "%DIST%"
del /q "%DIST%\WingetManager.exe" 2>nul
del /q "%DIST%\*.pdb" 2>nul
del /q "%DIST%\*.config" 2>nul
del /q "%DIST%\*.dll" 2>nul
copy /y "%BINDIR%\WingetManager.exe" "%DIST%\WingetManager.exe" >nul
if errorlevel 1 goto :copy_failed

rem --- offline mode leaves redundant framework copies in bin\Release ------
rem MSBuild 4.0 could not resolve the reference-assembly pack, so RAR fell
rem back to the runtime assemblies and marked them CopyLocal. They are pure
rem duplicates of what ships with .NET Framework; the shipped exe binds to
rem the GAC copies at runtime, so deleting them is safe and keeps bin clean.
if not defined OFFLINE goto :leak_check
del /q "%BINDIR%\mscorlib.dll" 2>nul
del /q "%BINDIR%\*.nlp" 2>nul
if exist "%BINDIR%\zh-Hans" rd /s /q "%BINDIR%\zh-Hans"
if exist "%BINDIR%\zh-Hant" rd /s /q "%BINDIR%\zh-Hant"
if exist "%BINDIR%\zh-CN" rd /s /q "%BINDIR%\zh-CN"

:leak_check

rem --- dependency leak check: a green build must not ship sibling DLL/EXE -
set "LEAK=0"
if exist "%BINDIR%\*.dll" set "LEAK=1"
if exist "%BINDIR%\*.config" set "LEAK=1"
if exist "%BINDIR%\*.pdb" set "LEAK=1"
if "%LEAK%"=="1" goto :leak_warn

echo [4/4] OK
echo.
for %%f in ("%DIST%\WingetManager.exe") do echo   File : %%~ff
for %%f in ("%DIST%\WingetManager.exe") do echo   Size : %%~zf bytes
echo   Deps : none (framework-only, embedded XAML + icon + manifest)
echo.
certutil -hashfile "%DIST%\WingetManager.exe" SHA256
echo.
echo  Copy dist\WingetManager.exe to any Windows machine with .NET Framework 4.8.
echo  Double-click to run. Nothing is written to the registry or Program Files.
echo.
endlocal
exit /b 0

:leak_warn
echo [4/4] WARNING - build output contains extra files besides the exe:
dir /b "%BINDIR%"
echo.
echo The single-file copy in dist\ is still valid, but please check why the
echo build produced sibling files (a NuGet reference was probably added).
endlocal
exit /b 0

:no_proj
echo [ERROR] Project file not found:
echo         %PROJ%
endlocal
exit /b 1

:no_msbuild
echo [ERROR] No usable MSBuild found.
echo.
echo   Either install one of these:
echo     - Visual Studio 2022 with the ".NET desktop development" workload
echo     - Visual Studio 2022 Build Tools + ".NET desktop build tools" component
echo.
echo   Or restore the offline toolchain (about 40 MB, no admin needed):
echo     tools\fetch-build-deps.bat
echo.
endlocal
exit /b 1

:no_deps
echo [ERROR] Offline toolchain is incomplete.
echo.
echo   Expected both of these to exist:
echo     tools\build-deps\roslyn\csc.exe
echo     tools\build-deps\refasm\build\.NETFramework\v4.8\mscorlib.dll
echo.
echo   Run:  tools\fetch-build-deps.bat
echo   It downloads the Roslyn compiler and the official .NET Framework 4.8
echo   reference assemblies into tools\build-deps (internet, no admin).
echo.
endlocal
exit /b 1

:build_failed
echo.
echo [ERROR] Build failed. See the MSBuild output above.
endlocal
exit /b 1

:no_output
echo [ERROR] Build reported success but %BINDIR%\WingetManager.exe is missing.
echo         Check the AssemblyName in WingetManagerWpf.csproj.
endlocal
exit /b 1

:copy_failed
echo [ERROR] Could not copy the exe into %DIST%.
echo         The most common cause is that WingetManager.exe is still running
echo         (close it and run this script again).
endlocal
exit /b 1
