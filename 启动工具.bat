@echo off
chcp 65001 >nul
title Winget 软件管家（开发模式）
cd /d "%~dp0"

rem 优先用带 PySide6 的解释器；找不到再逐个尝试
set "PYCMD="
for %%P in ("py -3" "python") do (
  %%~P -c "import PySide6" >nul 2>nul && (set "PYCMD=%%~P" & goto FOUND)
)
if exist "%LOCALAPPDATA%\Programs\Python\Python314\python.exe" (
  "%LOCALAPPDATA%\Programs\Python\Python314\python.exe" -c "import PySide6" >nul 2>nul && (
    set "PYCMD=%LOCALAPPDATA%\Programs\Python\Python314\python.exe" & goto FOUND
  )
)
goto NOPYSIDE

:FOUND
echo 使用解释器: %PYCMD%
%PYCMD% qt_app.py %*
if errorlevel 1 pause
goto :eof

:NOPYSIDE
echo.
echo   未找到安装了 PySide6 的 Python 3.9+。
echo   安装依赖:  pip install PySide6
echo   或直接运行打包好的 dist\WingetSoftwareManagerQt.exe
echo.
pause
goto :eof
