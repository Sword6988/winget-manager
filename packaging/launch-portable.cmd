@echo off
rem ---------------------------------------------------------------
rem Winget Manager portable launcher (executed by 7zSD.sfx inside the
rem temporary extraction directory).
rem
rem Portable contract: the SFX extracts the payload into %TEMP%\7zSxxxx,
rem runs this script, WAITS for it to exit, then deletes the extraction
rem directory. So this script must
rem   a) NOT keep the extraction dir as the current directory, otherwise
rem      7zSD cannot remove it (that is exactly where the empty
rem      %TEMP%\7zSxxxx shells of the installer build came from), and
rem   b) stay alive until the app closes, so the payload is not deleted
rem      under the app's feet -- hence "start /wait".
rem
rem Nothing is copied anywhere: the app runs straight out of %TEMP%.
rem Its user data (selection.json / metadata-cache / catalog-cache /
rem logs) still lives in %LOCALAPPDATA%\WingetManager -- that is data,
rem not an installation, and it is what keeps the selection and the
rem instant-start snapshot alive between runs.
rem
rem NOTE: keep this file ASCII-only, CRLF, no BOM.
rem ---------------------------------------------------------------
setlocal

set "APP=%~dp0WingetManager.exe"
if not exist "%APP%" (
    endlocal
    exit /b 1
)

rem Move off the extraction dir so 7zSD can delete it afterwards, then run
rem the app synchronously: "start /wait" waits for the GUI process to exit.
cd /d "%TEMP%"
start "" /wait "%APP%"

endlocal
exit /b 0
