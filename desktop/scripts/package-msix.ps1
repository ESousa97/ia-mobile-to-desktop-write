# Empacota Beam Desktop como MSIX (requer Windows SDK MakeAppx + SignTool para assinatura)
param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "$PSScriptRoot\..\dist\msix"
)

$ErrorActionPreference = "Stop"
$publishDir = "$PSScriptRoot\..\publish"
$layoutDir = "$OutputDir\layout"
$msixPath = "$OutputDir\Beam_0.1.0.0_x64.msix"

Write-Host "1/3 Publicando executável..."
& "$PSScriptRoot\publish.ps1" -Configuration $Configuration -OutputDir $publishDir

Write-Host "2/3 Montando layout MSIX..."
if (Test-Path $layoutDir) { Remove-Item $layoutDir -Recurse -Force }
New-Item -ItemType Directory -Path $layoutDir -Force | Out-Null
Copy-Item "$publishDir\*" $layoutDir -Recurse -Force
# Símbolos não vão no pacote distribuído: só inflam e expõem caminhos de build.
Get-ChildItem $layoutDir -Filter *.pdb -Recurse | Remove-Item -Force
# MakeAppx só reconhece o manifesto com este nome exato dentro do layout.
Copy-Item "$PSScriptRoot\..\installer\Package.appxmanifest" (Join-Path $layoutDir "AppxManifest.xml") -Force

$assetsDir = Join-Path $layoutDir "Assets"
New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null
# Placeholders 1x1 PNG (substitua por ícones reais antes da loja)
$png = [Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")
[IO.File]::WriteAllBytes((Join-Path $assetsDir "StoreLogo.png"), $png)
[IO.File]::WriteAllBytes((Join-Path $assetsDir "Square150x150Logo.png"), $png)
[IO.File]::WriteAllBytes((Join-Path $assetsDir "Square44x44Logo.png"), $png)

$makeAppx = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\makeappx.exe",
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\makeappx.exe",
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.19041.0\x64\makeappx.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $makeAppx) {
    Write-Warning "MakeAppx.exe não encontrado. Layout pronto em: $layoutDir"
    Write-Warning "Instale o Windows SDK e rode: makeappx pack /d `"$layoutDir`" /p `"$msixPath`""
    exit 0
}

New-Item -ItemType Directory -Path (Split-Path $msixPath) -Force | Out-Null
& $makeAppx pack /d $layoutDir /p $msixPath /o | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx falhou (exit code $LASTEXITCODE); o MSIX não foi gerado."
}

Write-Host "3/3 MSIX gerado (não assinado): $msixPath"
Write-Host "Assine com SignTool antes de distribuir: signtool sign /fd SHA256 /a `"$msixPath`""
