@echo off
setlocal EnableExtensions
rem =====================================================================
rem  Compatibility shim. The real script lives in SelfTest\.
rem
rem  This path used to hold its own full copy of the build logic, and the
rem  two copies drifted apart (different VS lookup lists, different
rem  assertion counts in the comments). To keep one source of truth this
rem  now just forwards to SelfTest\build-and-run.bat.
rem
rem  Usage:   build-and-run.bat
rem  Exit:    0 = all assertions passed, 1 = failures, 2 = no toolchain.
rem
rem  NOTE: keep this file ASCII-only. cmd.exe reads .bat with the OEM
rem  code page; a UTF-8 Chinese comment gets misparsed and can swallow
rem  the following backslash, which breaks every path in the script.
rem =====================================================================

set "TARGET=%~dp0SelfTest\build-and-run.bat"
if not exist "%TARGET%" goto :missing

call "%TARGET%" %*
exit /b %ERRORLEVEL%

:missing
echo [ERROR] Not found: %TARGET%
echo         The tests directory looks incomplete - re-clone the repo.
endlocal & exit /b 2
