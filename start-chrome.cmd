@echo off
setlocal

set "CHROME=%ProgramFiles%\Google\Chrome\Application\chrome.exe"
if not exist "%CHROME%" set "CHROME=%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"
set "DEBUG_DATA=%LOCALAPPDATA%\Chrome-CDP-Agent"
set "PROFILE_DIR=Agent CDP"

if not exist "%CHROME%" (
  echo Nie znaleziono pliku chrome.exe.
  echo Sprawdz instalacje Google Chrome, a nastepnie uruchom plik ponownie.
  pause
  exit /b 1
)

rem Chrome 136+ requires a non-default user-data directory for CDP.
rem Create a fresh, isolated Chrome profile on the first launch.
if not exist "%DEBUG_DATA%\%PROFILE_DIR%" (
  echo Tworzenie nowego profilu Agent CDP...
  mkdir "%DEBUG_DATA%\%PROFILE_DIR%" 2>nul
  > "%DEBUG_DATA%\%PROFILE_DIR%\Preferences" echo {"profile":{"name":"Agent CDP","is_using_default_name":false}}
)

start "Chrome Agent CDP - port 9222" "%CHROME%" ^
  --user-data-dir="%DEBUG_DATA%" ^
  --profile-directory="%PROFILE_DIR%" ^
  --remote-debugging-address=127.0.0.1 ^
  --remote-debugging-port=9222
  -remote-allow-origins=http://localhost

endlocal
