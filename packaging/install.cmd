@echo off
rem ---------------------------------------------------------------
rem Winget Manager SFX install script (executed by 7zSD.sfx inside
rem the temporary extraction directory).
rem Copies the full publish payload to the persistent install dir,
rem then launches the app. The SFX deletes this temp dir afterwards.
rem NOTE 1: keep this file ASCII-only -- cmd.exe parses batch files
rem         in the ANSI codepage (GBK on zh-CN); UTF-8 comments break it.
rem NOTE 2: do NOT put a trailing backslash inside a quoted path
rem         ("...dir\" -- the backslash escapes the closing quote in
rem         CRT argument parsing and xcopy/robocopy get broken args).
rem ---------------------------------------------------------------
setlocal

rem Persistent per-user install dir (same convention as winget/pip tools)
set "TARGET=%LOCALAPPDATA%\Programs\WingetManager"

rem Source = temp extraction dir; strip the trailing backslash
set "SRC=%~dp0"
set "SRC=%SRC:~0,-1%"

rem robocopy: /e copy subdirs incl. empty; /purge remove obsolete files
rem (clean upgrade); /r:1 /w:1 fail fast on in-use files instead of the
rem 30-retries-per-file default; /nfl /ndl /njh /njs /np quiet.
rem /xf install.cmd keeps the launcher itself out of the install dir
rem (note: /xf exempts the file from /purge, so delete it explicitly).
if exist "%TARGET%\install.cmd" del "%TARGET%\install.cmd" >nul 2>&1
robocopy "%SRC%" "%TARGET%" /e /purge /r:1 /w:1 /xf install.cmd /nfl /ndl /njh /njs /np >nul

rem Launch from the install dir; start returns immediately so cmd exits
rem and the SFX cleans up the temp dir while the app keeps running.
start "" "%TARGET%\WingetManager.exe"

endlocal
