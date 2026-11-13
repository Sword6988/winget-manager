@echo off
rem ---------------------------------------------------------------
rem Winget Manager SFX install script (executed by 7zSD.sfx inside
rem the temporary extraction directory).
rem Stops a running instance (an open exe would be skipped by robocopy
rem and leave a mixed install: new DLLs with the old exe), copies the
rem payload to the persistent install dir, then launches the app.
rem The SFX deletes this temp dir after this script exits.
rem NOTE 1: keep this file ASCII-only, CRLF, no BOM -- cmd.exe parses
rem         batch files in the ANSI codepage (GBK on zh-CN).
rem NOTE 2: do NOT put a trailing backslash inside a quoted path
rem         ("...dir\" -- the backslash escapes the closing quote in
rem         CRT argument parsing and robocopy gets broken args).
rem ---------------------------------------------------------------
setlocal

set "TARGET=%LOCALAPPDATA%\Programs\WingetManager"
set "LOG=%TEMP%\WingetManager-install.log"

rem Source = temp extraction dir; strip the trailing backslash
set "SRC=%~dp0"
set "SRC=%SRC:~0,-1%"

rem 1) Stop a running instance: ask politely first (lets the app save its
rem    window bounds), then force-kill anything left. Failures are fine
rem    (not running / no rights) -- the install must not abort here.
taskkill /im WingetManager.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul
taskkill /f /im WingetManager.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul

rem 2) Copy payload -> install dir.
rem    /e subdirs incl. empty; /purge drop obsolete files (clean upgrade);
rem    /r:1 /w:1 fail fast instead of 30 retries per in-use file;
rem    /xf install.cmd keeps the launcher out of the install dir
rem    (/xf also exempts it from /purge, so delete it explicitly).
if exist "%TARGET%\install.cmd" del "%TARGET%\install.cmd" >nul 2>&1
robocopy "%SRC%" "%TARGET%" /e /purge /r:1 /w:1 /xf install.cmd /nfl /ndl /njh /njs /np /log+:"%LOG%" >nul

rem 3) robocopy exit codes: 0 = nothing to do, 1-7 = success (files
rem    copied / extra / mismatch resolved), 8+ = files or dirs could not
rem    be copied -- abort loudly instead of silently starting a mixed
rem    install.
if errorlevel 8 goto failed

rem 4) Launch from the install dir; start returns immediately so this
rem    script exits and the SFX cleans up the temp dir while the app runs.
start "" "%TARGET%\WingetManager.exe"
endlocal
exit /b 0

:failed
rem The GUI SFX swallows our exit code and has no console to print into,
rem so surface the failure by opening the robocopy log in Notepad.
rem (Text is English by design: non-ASCII in a batch file gets mangled
rem by the GBK codepage.)
echo ============================================================>>"%LOG%"
echo INSTALL FAILED: files could not be copied to>>"%LOG%"
echo   %TARGET%>>"%LOG%"
echo Close Winget Manager (or any file viewer holding the files), then>>"%LOG%"
echo run this installer again. Error details are below this line.>>"%LOG%"
echo ============================================================>>"%LOG%"
start "" notepad "%LOG%"
endlocal
exit /b 1
