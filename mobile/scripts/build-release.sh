#!/usr/bin/env bash
# Build release APK (requer keystore.properties — veja keystore.properties.example)
set -euo pipefail
cd "$(dirname "$0")"
./gradlew assembleRelease --no-daemon
echo "APK: app/build/outputs/apk/release/"
