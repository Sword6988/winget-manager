@echo off
REM ════════════════════════════════════════════════════════════════════
REM WingetManagerWpf 逻辑自测一键脚本。
REM
REM 注意：开发 QA 沙箱里没有 MSBuild / .NET SDK / csc，本脚本在那台机器上
REM 会走到「未找到 MSBuild」分支并给出手动指引。请在装有
REM VS2022（或 VS2022 Build Tools，含 .NET 桌面开发工作负载）的机器上运行。
REM
REM 也可以手动执行：
REM   1. 打开「Developer Command Prompt for VS2022」
REM   2. cd /d <本目录>
REM   3. msbuild SelfTest.csproj /p:Configuration=Debug /nologo /v:m
REM   4. bin\Debug\SelfTest.exe
REM   5. 检查输出末尾「结果: PASS」与退出码（0=全部通过，1=有失败）
REM ════════════════════════════════════════════════════════════════════
setlocal
cd /d "%~dp0"

set "MSBUILD="
if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" (
    for /f "usebackq tokens=*" %%i in (
        `"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`
    ) do set "MSBUILD=%%i"
)

if "%MSBUILD%"=="" (
    echo [SelfTest] 未在本机找到 MSBuild（没有 VS2022 / Build Tools）。
    echo [SelfTest] 请在装了 VS2022 的机器上执行：
    echo     msbuild SelfTest.csproj /p:Configuration=Debug
    echo     bin\Debug\SelfTest.exe
    exit /b 2
)

echo [SelfTest] 使用 MSBuild: %MSBUILD%
"%MSBUILD%" SelfTest.csproj /p:Configuration=Debug /nologo /v:m
if errorlevel 1 (
    echo [SelfTest] 编译失败。
    exit /b 1
)

echo [SelfTest] 运行 bin\Debug\SelfTest.exe ...
bin\Debug\SelfTest.exe
set RC=%ERRORLEVEL%
if "%RC%"=="0" (
    echo [SelfTest] 全部通过。
) else (
    echo [SelfTest] 存在失败断言，退出码 %RC%。
)
exit /b %RC%
