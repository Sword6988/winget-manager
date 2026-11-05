@echo off
rem Build WingetPackageMgr.exe with PyInstaller.
rem Requires: Python 3.9+ (with tkinter) on PATH.
cd /d "%~dp0"

python -m pip install --quiet pyinstaller || goto :fail

python -m PyInstaller --noconfirm --clean --onefile --windowed ^
  --name "WingetPackageMgr" gui_app.py || goto :fail

echo.
echo Build OK: dist\WingetPackageMgr.exe
pause
exit /b 0

:fail
echo Build FAILED. Check messages above.
pause
exit /b 1
