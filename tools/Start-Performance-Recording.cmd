@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0dot-orbit.exe" (
  echo Put this launcher beside dot-orbit.exe in the extracted diagnostic package.
  pause
  exit /b 1
)
set "TRACE_ID=%RANDOM%-%RANDOM%-%RANDOM%"
set "TRACE_FILE=%~dp0dot-orbit-performance-%TRACE_ID%.json"
if exist "%TRACE_FILE%" (
  echo That recording name already exists. Run this launcher again for a new name.
  pause
  exit /b 1
)
echo Close any other dot-orbit window before continuing.
echo This records timing metadata locally. Nothing is uploaded.
echo Use the app for 1-2 minutes: toggle Today, start/stop, move tasks, and edit.
echo Then close dot-orbit normally so the timing file can be written.
pause
start "" /wait "%~dp0dot-orbit.exe" --performance-trace --default-workspace --performance-trace-id=%TRACE_ID%
if not exist "%TRACE_FILE%" goto no_recording
for %%F in ("%TRACE_FILE%") do if %%~zF LSS 1 goto no_recording
echo.
echo Recording saved. Return this JSON file:
echo "%TRACE_FILE%"
echo No workspace or recovery file is needed.
pause
exit /b 0
:no_recording
echo No completed recording was saved. Do not send an older file.
echo Extract to a writable folder and close dot-orbit normally after recording.
pause
exit /b 1
