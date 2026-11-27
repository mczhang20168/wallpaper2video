@echo off
setlocal
cd /d "%~dp0"
if exist "%~dp0.dotnet\dotnet.exe" set "DOTNET_ROOT=%~dp0.dotnet"
set "W2V_DESKTOP=%~dp0csharp\src\Wallpaper2Video.Desktop\bin\Release\net10.0-windows\Wallpaper2Video.Desktop.exe"
if not exist "%W2V_DESKTOP%" (
  echo Build first: powershell -NoProfile -File csharp\build.ps1
  pause
  exit /b 1
)
start "" "%W2V_DESKTOP%" %*
