@echo off
setlocal
set "STATUSDOCK_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
set "STATUSDOCK_APP=%~dp0StatusDock.ps1"
echo.
echo   StatusDock for Flipper Zero
echo.
echo   1  Start StatusDock now
echo   2  Start automatically at Windows sign-in
echo   3  Stop StatusDock
echo   4  Remove automatic startup
echo   5  Create or update the desktop shortcut
echo.
choice /c 12345 /n /m "Choose 1-5: "
if errorlevel 255 exit /b 1
if errorlevel 5 goto shortcut
if errorlevel 4 goto remove
if errorlevel 3 goto stop
if errorlevel 2 goto install
if errorlevel 1 goto run
exit /b

:shortcut
"%STATUSDOCK_PS%" -NoProfile -ExecutionPolicy Bypass -File "%STATUSDOCK_APP%" -InstallDesktopShortcut
goto done

:remove
"%STATUSDOCK_PS%" -NoProfile -ExecutionPolicy Bypass -File "%STATUSDOCK_APP%" -RemoveStartup
goto done

:stop
"%STATUSDOCK_PS%" -NoProfile -ExecutionPolicy Bypass -File "%STATUSDOCK_APP%" -Stop
goto done

:install
"%STATUSDOCK_PS%" -NoProfile -ExecutionPolicy Bypass -File "%STATUSDOCK_APP%" -InstallStartup
goto done

:run
"%STATUSDOCK_PS%" -NoProfile -ExecutionPolicy Bypass -File "%STATUSDOCK_APP%"

:done
pause
