@echo off
setlocal
rem Installs the YouTube Video Player skill, with Wingman Player inside it,
rem into Wingman AI's custom_skills folder. Run it from the unzipped download.

set "SRC=%~dp0youtube_video_player"
set "WINGMAN=%APPDATA%\ShipBit\WingmanAI"
set "DEST=%WINGMAN%\custom_skills\youtube_video_player"

if not exist "%SRC%\main.py" (
    echo Can't find the youtube_video_player folder next to install.bat.
    echo Unzip the whole download first, then run install.bat from there.
    goto :failed
)
if not exist "%WINGMAN%" (
    echo Wingman AI isn't set up for this Windows user: %WINGMAN% is missing.
    echo Install and start Wingman AI once, then run install.bat again.
    goto :failed
)

echo Closing Wingman Player if it is running...
taskkill /F /IM Wingman-Player.exe >nul 2>&1
ping -n 3 127.0.0.1 >nul

echo Copying to %DEST%
robocopy "%SRC%" "%DEST%" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
    echo Copying failed. Close Wingman AI and Wingman Player, then try again.
    goto :failed
)

echo.
echo Done. Restart Wingman AI, then switch on "YouTube Video Player" in a
echo Wingman's Skills. The skill's page there explains the free YouTube API key.
echo.
echo If you installed Wingman Player with the old .msi installer, you can
echo uninstall that one in Windows Settings, Apps. It isn't needed any more.
pause
exit /b 0

:failed
pause
exit /b 1
