# Gera build publicável do ClipBridge Desktop (self-contained, win-x64)
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "$PSScriptRoot\..\publish"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\Beam.Desktop\Beam.Desktop.csproj"

Write-Host "Publicando ClipBridge Desktop ($Configuration / $Runtime)..."
dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    throw "Falha ao publicar o Beam (exit code $LASTEXITCODE)."
}

Write-Host "Pronto: $OutputDir\Beam.exe"
