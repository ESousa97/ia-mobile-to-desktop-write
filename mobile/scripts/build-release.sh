#!/usr/bin/env bash
# Build release do app Android (requer mobile/keystore.properties).
# APK e AAB assinados vão para dist/mobile na raiz do repositório.
set -euo pipefail

mobile_root="$(cd "$(dirname "$0")/.." && pwd)"
repo_root="$(cd "$mobile_root/.." && pwd)"
dist_dir="$repo_root/dist/mobile"
outputs="$mobile_root/app/build/outputs"

if [[ ! -f "$mobile_root/keystore.properties" ]]; then
  echo "mobile/keystore.properties não encontrado — copie keystore.properties.example e aponte para o seu keystore." >&2
  exit 1
fi

version_name="$(sed -n 's/.*versionName[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' "$mobile_root/app/build.gradle.kts" | head -n 1)"
if [[ -z "$version_name" ]]; then
  echo "Não foi possível ler versionName de app/build.gradle.kts." >&2
  exit 1
fi

cd "$mobile_root"
./gradlew assembleRelease bundleRelease --no-daemon

mkdir -p "$dist_dir"
cp "$outputs/apk/release/app-release.apk" "$dist_dir/beam-$version_name.apk"
cp "$outputs/bundle/release/app-release.aab" "$dist_dir/beam-$version_name.aab"

echo "Pronto: $dist_dir"
