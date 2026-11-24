@echo off
rem Starts the bot from the folder this file is in.
rem Pass --data-dir <folder> to keep config.json and secrets.json somewhere else than the default.
"%~dp0Kzg.Bot.exe" %*
exit /b %ERRORLEVEL%
