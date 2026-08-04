# Gera build publicável do Beam Desktop (self-contained, single-file).
# O executável final vai para dist/desktop na raiz do repositório; o publish cru
# fica em dist/.staging (intermediário, limpo por scripts/build-release.ps1).
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$project  = Join-Path $repoRoot "desktop\src\Beam.Desktop\Beam.Desktop.csproj"
$distDir  = Join-Path $repoRoot "dist\desktop"
$stageDir = Join-Path $repoRoot "dist\.staging\publish-$Runtime"

$version = (Select-Xml -Path $project -XPath '//Version').Node.InnerText | Select-Object -First 1
if (-not $version) { throw "Não foi possível ler <Version> de $project." }

Write-Host "Publicando Beam Desktop $version ($Configuration / $Runtime)..."
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $stageDir

if ($LASTEXITCODE -ne 0) {
    throw "Falha ao publicar o Beam (exit code $LASTEXITCODE)."
}

New-Item -ItemType Directory -Path $distDir -Force | Out-Null
$target = Join-Path $distDir "Beam-$version-$Runtime.exe"
Copy-Item (Join-Path $stageDir "Beam.exe") $target -Force

Write-Host "Pronto: $target"
