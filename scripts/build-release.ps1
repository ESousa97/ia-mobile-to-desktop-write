# Reúne todos os artefatos de release do Beam em dist/ na raiz do repositório.
#
#   dist/desktop/  Beam-<ver>-win-x64.exe, Beam-<ver>-x64-unsigned.msix
#   dist/mobile/   beam-<ver>.apk, beam-<ver>.aab
#   dist/SHA256SUMS.txt
#
# Ao terminar, nenhum binário sobra fora de dist/: o staging e os intermediários
# de build (bin/, obj/, app/build/) são apagados por scripts/clean.ps1 — use
# -KeepIntermediates para preservá-los e manter os builds seguintes incrementais.
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipDesktop,
    [switch]$SkipMsix,
    [switch]$SkipMobile,
    [switch]$KeepIntermediates
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$distDir  = Join-Path $repoRoot "dist"
$stageDir = Join-Path $distDir ".staging"

Write-Host "== Limpando dist/ ==" -ForegroundColor Cyan
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

if (-not $SkipDesktop) {
    Write-Host "== Desktop ==" -ForegroundColor Cyan
    if ($SkipMsix) {
        & (Join-Path $repoRoot "desktop\scripts\publish.ps1") -Configuration $Configuration
    }
    else {
        # package-msix.ps1 já chama publish.ps1 e reaproveita o mesmo staging.
        & (Join-Path $repoRoot "desktop\scripts\package-msix.ps1") -Configuration $Configuration
    }
}

if (-not $SkipMobile) {
    Write-Host "== Mobile ==" -ForegroundColor Cyan
    & (Join-Path $repoRoot "mobile\scripts\build-release.ps1")
}

if ($KeepIntermediates) {
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
}
else {
    Write-Host "== Higienizando resíduos de build ==" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "clean.ps1")
}

Write-Host "== Checksums ==" -ForegroundColor Cyan
$artifacts = Get-ChildItem $distDir -Recurse -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Sort-Object FullName

if (-not $artifacts) { throw "Nenhum artefato gerado em $distDir." }

$lines = foreach ($a in $artifacts) {
    $relative = $a.FullName.Substring($distDir.Length + 1).Replace('\', '/')
    "{0}  {1}" -f (Get-FileHash $a.FullName -Algorithm SHA256).Hash, $relative
}
Set-Content -Path (Join-Path $distDir "SHA256SUMS.txt") -Value $lines -Encoding utf8NoBOM

Write-Host ""
Write-Host "Release completo em $distDir" -ForegroundColor Green
$artifacts | ForEach-Object {
    "  {0,-40} {1,8:N1} MB" -f $_.FullName.Substring($distDir.Length + 1), ($_.Length / 1MB)
}
