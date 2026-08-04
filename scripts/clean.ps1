# Remove todo resíduo de build do repositório: bin/, obj/, app/build/ e o staging
# de release. Com -All, apaga também dist/ e os caches locais do Gradle (o que
# obriga uma reconfiguração completa no próximo build).
[CmdletBinding()]
param(
    [switch]$All
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$targets = [System.Collections.Generic.List[string]]::new()

# .NET: bin/ e obj/ de cada projeto do desktop.
Get-ChildItem (Join-Path $repoRoot "desktop\src") -Directory -Recurse -Include bin, obj -ErrorAction SilentlyContinue |
    ForEach-Object { $targets.Add($_.FullName) }

# Android: saídas de build do Gradle.
$targets.Add((Join-Path $repoRoot "mobile\build"))
$targets.Add((Join-Path $repoRoot "mobile\app\build"))

# Intermediários de empacotamento do release.
$targets.Add((Join-Path $repoRoot "dist\.staging"))

# Diretórios de publish legados, de antes da centralização em dist/.
$targets.Add((Join-Path $repoRoot "desktop\publish"))
$targets.Add((Join-Path $repoRoot "desktop\dist"))

if ($All) {
    $targets.Add((Join-Path $repoRoot "dist"))
    $targets.Add((Join-Path $repoRoot "mobile\.gradle"))
    $targets.Add((Join-Path $repoRoot "mobile\.kotlin"))
}

$removed = 0
foreach ($t in $targets) {
    if (Test-Path $t) {
        Remove-Item $t -Recurse -Force
        Write-Host "  removido: $($t.Substring($repoRoot.Length + 1))"
        $removed++
    }
}

Write-Host "$removed diretório(s) removido(s)."
