@echo off
rem Double-click after every new WebGL build: puts the game online at play.catbattlegrounds.com (see deploy.mjs).
cd /d "%~dp0"
if not exist node_modules call npm install --no-audit --no-fund
node deploy.mjs
echo.
pause
