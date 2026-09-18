@echo off
setlocal
set "SHARPEMU_AUTO_CROSS="
set "SHARPEMU_TRACE_GUEST_IMAGES="
set "SHARPEMU_SWAPCHAIN_DUMP_EVERY="
set "SHARPEMU_LOG_VK_RESOURCES="
pushd "%~dp0artifacts\boot-investigation"
"%~dp0artifacts\bin\Release\net10.0\win-x64\SharpEmu.exe" --log-level=info "--log-file=%~dp0artifacts\boot-investigation\play.log" "C:\Users\chayt\Downloads\PSX\eboot.bin"
set "SHARPEMU_GAME_EXIT=%ERRORLEVEL%"
popd
if not "%SHARPEMU_GAME_EXIT%"=="0" pause
exit /b %SHARPEMU_GAME_EXIT%
