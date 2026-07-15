# Build release APK (requer mobile/keystore.properties)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot\..
.\gradlew.bat assembleRelease --no-daemon
Write-Host "APK em app\build\outputs\apk\release\"
