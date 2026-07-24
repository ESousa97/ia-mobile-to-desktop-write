# Beam

Local clipboard sync between Windows and Android—text and images, end to end, without leaving your network.

[![Desktop CI](https://github.com/ESousa97/ia-mobile-to-desktop-write/actions/workflows/desktop-ci.yml/badge.svg)](https://github.com/ESousa97/ia-mobile-to-desktop-write/actions/workflows/desktop-ci.yml)
[![Android CI](https://github.com/ESousa97/ia-mobile-to-desktop-write/actions/workflows/android-ci.yml/badge.svg)](https://github.com/ESousa97/ia-mobile-to-desktop-write/actions/workflows/android-ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## What Beam is

Beam keeps the clipboard of a Windows PC and an Android phone in sync over your local network. Copy text or an image on one device and it appears on the other. There is no cloud relay, no account, and no third-party service in the path: traffic stays on your Wi‑Fi or LAN.

The Windows app also exposes two global hotkeys for desktop-only workflows that do not rely on remote control:

| Shortcut | Action |
|----------|--------|
| `Ctrl + F` | Capture a full-resolution screenshot in the background and send it to the phone |
| `Ctrl + F1` | Type the current clipboard text via simulated keystrokes instead of pasting |

Simulated typing exists for fields that block `Ctrl+V` (banking apps, some terminals, locked-down forms). Beam injects Unicode input character by character after you release the modifier keys.

## Capabilities

- Bidirectional clipboard sync for text and images
- Full-resolution screenshots from the PC, with zoom and pan on the phone
- Local keyboard injection on Windows (`Ctrl+F1`), never driven by a remote command
- End-to-end session encryption (X25519 key agreement, HKDF, AES-256-GCM)
- Automatic discovery on the LAN via UDP broadcast—no manual IP entry for normal use
- Pairing with a six-digit code shown on the desktop
- Native UI: Fluent Design on Windows 11 (WPF + WPF-UI) and Material 3 on Android (Jetpack Compose)
- Foreground service on Android to keep the connection alive in the background
- Tray icon on Windows so the server can keep running while minimized

## How it works

```
┌────────────────────────────┐         Wi-Fi / LAN          ┌────────────────────────────┐
│       Beam Desktop         │  ←── WebSocket + AES-GCM ──→ │        Beam Mobile         │
│    (Windows · .NET 9)      │                              │    (Android · Kotlin)      │
│                            │                              │                            │
│  WebSocket server          │   UDP discovery (:8788)      │  WebSocket client          │
│  Clipboard watcher         │   Six-digit pairing code     │  Clipboard watcher         │
│  Global hotkeys            │                              │  Beam keyboard (IME)       │
│  Screen capture            │                              │  Screenshot viewer (HD)    │
│  Local SendInput typing    │                              │  Foreground sync service   │
└────────────────────────────┘                              └────────────────────────────┘
```

The desktop is the server (stable host on the LAN). The phone is the client: it discovers the desktop, completes pairing, and maintains a durable session.

Deeper material lives under `docs/`:

- [Architecture](docs/ARCHITECTURE.md)
- [Protocol](docs/PROTOCOL.md)
- [Security design](docs/SECURITY-DESIGN.md)
- [Threat model](docs/THREAT-MODEL.md)

Default ports: WebSocket `8787`, UDP discovery `8788`, UDP announce `8789`.

### Connecting over Wi-Fi

Both devices must sit on the same Wi-Fi network. On first run:

1. Open the desktop window and check the **"Acesso pela Wi-Fi"** card — it verifies the Windows Firewall inbound rules and creates them on demand (UAC prompt). Without them Windows silently drops the phone's connection, especially on networks classified as "Public".
2. The phone finds the desktop by UDP broadcast, in both directions (probe and announce), so a firewall blocking one direction is not fatal.
3. If discovery still fails — client isolation, guest VLANs, or a router that filters broadcast — use **"Informar IP manualmente"** on the phone and type the `ip:port` shown in the desktop window. Pairing and sync work the same way.

You type the pairing code once. After that the phone reconnects on its own — after a Wi-Fi drop, a reboot, or the desktop being switched off — for **72 hours from the last successful connection**, and every reconnection renews that window. Revoke it any time with **"Revogar dispositivos"** in the desktop window, which also drops live sessions. See [Security design](docs/SECURITY-DESIGN.md#sessões-e-reconexão-automática).

## Repository layout

```
.
├─ desktop/                 Windows app (.NET 9 / WPF)
│  ├─ Beam.sln
│  ├─ scripts/              publish.ps1, package-msix.ps1
│  ├─ installer/            MSIX package manifest
│  └─ src/
│     ├─ Beam.Core/         Protocol, crypto, networking (UI-agnostic)
│     ├─ Beam.Desktop/      WPF UI and Windows services
│     └─ Beam.Core.Tests/   xUnit tests
├─ mobile/                  Android app (Kotlin / Compose)
│  ├─ app/
│  ├─ keystore.properties.example
│  └─ scripts/              release build helpers
├─ docs/                    Architecture, protocol, security
└─ .github/workflows/       Desktop CI, Android CI, CodeQL, release on tag
```

Local publish output (`desktop/publish/`, `desktop/publish-*/`, `desktop/dist/`) and binaries (`.exe`, `.pdb`, `.dll`, MSIX packages) are gitignored. Build artifacts are produced by scripts or CI—they are not committed.

## Getting started

### Prerequisites

- **Desktop:** [.NET 9 SDK](https://dotnet.microsoft.com/download) on Windows 10 or 11
- **Mobile:** [Android Studio](https://developer.android.com/studio) (recent stable) and JDK 17

### Run the desktop app

```bash
cd desktop
dotnet build Beam.sln
dotnet run --project src/Beam.Desktop
```

### Run the Android app

Open `mobile/` in Android Studio, or from a terminal:

```bash
cd mobile
./gradlew assembleDebug installDebug
```

For a signed release build, copy `keystore.properties.example` to `keystore.properties` (never commit that file), point it at your keystore, then run `./gradlew assembleRelease`. Helper scripts live in `mobile/scripts/`.

## First-run walkthrough

1. Start Beam on the PC. Note the six-digit pairing code on the main window.
2. Start Beam on the phone. Join the same network, discover the desktop, and enter the code.
3. On Android, open **Beam Keyboard** → **Configure**, enable the IME in system settings, and select it as the default input method. Android only allows the current IME to observe new clipboard copies while the app is in the background; this step is required for reliable phone-to-PC text sync.
4. On the PC, press `Ctrl + F` to capture the screen and send it to the phone.
5. Copy text on the phone. It should land on the Windows clipboard. Focus the destination field and press `Ctrl + F1`. Beam waits until `Ctrl` is released, then types the text as physical keyboard input (it does not paste).

Note on elevation: Windows will not inject keystrokes from a non-elevated process into an elevated one. If the target app runs as Administrator, run Beam Desktop as Administrator as well.

## Publishing the desktop build

Portable self-contained executable:

```powershell
cd desktop/scripts
./publish.ps1
# Output: desktop/publish/Beam.exe
```

MSIX layout (requires the Windows SDK tooling on the machine):

```powershell
cd desktop/scripts
./package-msix.ps1
# Output: desktop/dist/msix/
```

Tag a version (`v*`) to trigger [`.github/workflows/release.yml`](.github/workflows/release.yml), which builds desktop artifacts and a debug Android APK and attaches them to a GitHub Release. Store signing for release APKs and MSIX certificates remains a manual follow-up (GitHub Secrets / signing cert); templates and Gradle wiring are already in place.

## Project status

| Area | State |
|------|--------|
| Protocol, pairing, encrypted sessions | Done |
| Bidirectional text and image clipboard | Done |
| High-resolution screenshots (desktop → mobile) | Done |
| Android foreground session, heartbeat, auto-rediscovery | Done |
| Local simulated typing on Windows | Done |
| Tray minimize, HD image viewer | Done |
| Portable publish script and MSIX packaging layout | Done |
| Release workflow on `v*` tags | Done |
| Optional Android release signing via `keystore.properties` | Done |
| Signed release APK in CI | Planned (needs keystore secrets) |
| MSIX / Microsoft Store signing | Planned |

## Security

Clipboard contents are treated as sensitive. Pairing establishes an ephemeral key agreement; application payloads are encrypted with AES-256-GCM even on a trusted LAN.

Read [SECURITY.md](SECURITY.md) for the vulnerability reporting process, and [docs/THREAT-MODEL.md](docs/THREAT-MODEL.md) for assumptions and non-goals. Secrets, keystores, and certificates must stay out of Git—see [`.gitignore`](.gitignore).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/).

## License

[MIT](LICENSE) © 2026 ESousa97
