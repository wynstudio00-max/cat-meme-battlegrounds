@echo off
rem Double-click: puts the auto-reveal Worker online on Cloudflare (see deploy.mjs). Safe to run again after code changes.
cd /d "%~dp0"
if not exist node_modules call npm install --no-audit --no-fund
node deploy.mjs
echo.
pause
