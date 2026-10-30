  @echo off
  setlocal

  cd /d "%~dp0"

  set "Rig2Cast__StationConfig=%~dp0station.json"
  set "Rig2Cast__AllowWrites=true"
  set "ASPNETCORE_URLS=http://127.0.0.1:8080"

  echo Starting Rig2Cast Web GUI...
  echo Station: %Rig2Cast__StationConfig%
  echo Address: http://127.0.0.1:8080
  echo.

  dotnet run --project "Rig2Cast.WebGui.csproj" --no-launch-profile

  if errorlevel 1 (
      echo.
      echo Rig2Cast failed to start.
      pause
  )

  endlocal