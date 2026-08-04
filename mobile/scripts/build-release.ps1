# Build release do app Android (requer mobile/keystore.properties).
# APK e AAB assinados vão para dist/mobile na raiz do repositório.
[CmdletBinding()]
param(
    [switch]$SkipBundle
)

$ErrorActionPreference = "Stop"

$mobileRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$repoRoot   = (Resolve-Path (Join-Path $mobileRoot "..")).Path
$distDir    = Join-Path $repoRoot "dist\mobile"
$outputs    = Join-Path $mobileRoot "app\build\outputs"

if (-not (Test-Path (Join-Path $mobileRoot "keystore.properties"))) {
    throw "mobile/keystore.properties não encontrado — copie keystore.properties.example e aponte para o seu keystore."
}

$gradleFile  = Join-Path $mobileRoot "app\build.gradle.kts"
$versionName = ([regex]::Match((Get-Content $gradleFile -Raw), 'versionName\s*=\s*"([^"]+)"')).Groups[1].Value
if (-not $versionName) { throw "Não foi possível ler versionName de $gradleFile." }

$tasks = @("assembleRelease")
if (-not $SkipBundle) { $tasks += "bundleRelease" }

Write-Host "Buildando Beam Android $versionName ($($tasks -join ', '))..."
Push-Location $mobileRoot
try {
    & (Join-Path $mobileRoot "gradlew.bat") @tasks --no-daemon
    if ($LASTEXITCODE -ne 0) { throw "Gradle falhou (exit code $LASTEXITCODE)." }
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Path $distDir -Force | Out-Null
Copy-Item (Join-Path $outputs "apk\release\app-release.apk") (Join-Path $distDir "beam-$versionName.apk") -Force
if (-not $SkipBundle) {
    Copy-Item (Join-Path $outputs "bundle\release\app-release.aab") (Join-Path $distDir "beam-$versionName.aab") -Force
}

Write-Host "Pronto: $distDir"
