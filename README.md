<div align="center">

# 🔗 ClipBridge

**Área de transferência compartilhada entre Windows e Android — texto e imagens, em tempo real, 100% na sua rede local.**

[![Desktop CI](https://github.com/ESousa97/clipbridge/actions/workflows/desktop-ci.yml/badge.svg)](https://github.com/ESousa97/clipbridge/actions/workflows/desktop-ci.yml)
[![Android CI](https://github.com/ESousa97/clipbridge/actions/workflows/android-ci.yml/badge.svg)](https://github.com/ESousa97/clipbridge/actions/workflows/android-ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Kotlin](https://img.shields.io/badge/Kotlin-Compose-7F52FF?logo=kotlin&logoColor=white)](https://kotlinlang.org/)

</div>

---

## ✨ O que é

O **ClipBridge** mantém a área de transferência do seu **PC (Windows)** e do seu **celular (Android)** em sincronia. Copiou um texto ou imagem no PC? Aparece no celular. E vice-versa. Sem nuvem, sem conta, sem seus dados saindo da sua rede Wi-Fi.

Além disso, o app de desktop traz dois superpoderes acionados por atalho global:

| Atalho | Ação |
|--------|------|
| `Ctrl + F` | 📸 Captura a tela em **alta resolução** (full-res, sem perda) em segundo plano e envia para o celular |
| `Ctrl + F1` | ⌨️ **Digita** o texto copiado (em vez de colar) — simula o teclado físico, funcionando em qualquer campo/app |

> Por que "digitar" em vez de "colar"? Alguns campos bloqueiam `Ctrl+V` (bancos, terminais, formulários). O ClipBridge emula o teclado caractere a caractere, contornando essas restrições.

## 🎯 Principais recursos

- 🔄 **Sync bidirecional** de texto e imagens (PC ↔ Android)
- 📸 **Screenshots em alta definição** com zoom/pan no celular
- ⌨️ **Digitação simulada** do conteúdo copiado em qualquer lugar do desktop
- 🔒 **Criptografia ponta-a-ponta** (AES-256-GCM) — nem em LAN o tráfego fica exposto
- 📡 **Descoberta automática** via mDNS/NSD — sem digitar IP
- 🔗 **Pareamento por QR Code** — seguro e à prova de erro
- 🎨 **UI nativa e polida** — Fluent Design (Windows 11) e Material 3 (Android)
- 🚫 **Zero nuvem** — nada trafega fora da sua rede local

## 🏗️ Arquitetura em 30 segundos

```
┌────────────────────────┐         Wi-Fi / LAN          ┌────────────────────────┐
│   ClipBridge Desktop    │  ←── WebSocket + AES-GCM ──→ │   ClipBridge Mobile     │
│   (Windows · .NET 9)    │                              │   (Android · Kotlin)    │
│                         │                              │                         │
│  • Servidor WebSocket   │   descoberta via mDNS        │  • Cliente WebSocket    │
│  • Clipboard watcher    │   pareamento via QR          │  • Clipboard watcher    │
│  • Hotkeys globais      │                              │  • Visualizador de      │
│  • Captura de tela      │                              │    screenshots (HD)     │
│  • Digitação (SendInput)│                              │  • Scanner de QR        │
└────────────────────────┘                              └────────────────────────┘
```

O **desktop é o servidor** (fica ligado, tem IP estável na LAN); o **celular é o cliente**. Detalhes completos em [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), o protocolo em [`docs/PROTOCOL.md`](docs/PROTOCOL.md) e o modelo de segurança em [`docs/SECURITY-DESIGN.md`](docs/SECURITY-DESIGN.md).

## 📁 Estrutura do repositório

```
clipbridge/
├─ desktop/            # App Windows — C# / .NET 9 / WPF + WPF-UI (Fluent)
│  └─ src/
│     ├─ ClipBridge.Core/        # Protocolo, cripto, rede, descoberta (agnóstico de UI)
│     ├─ ClipBridge.Desktop/     # UI WPF + serviços Windows (clipboard, hotkey, tela, digitação)
│     └─ ClipBridge.Core.Tests/  # Testes unitários (xUnit)
├─ mobile/             # App Android — Kotlin / Jetpack Compose / Material 3
│  └─ app/src/main/...
├─ docs/               # Arquitetura, protocolo, segurança, modelo de ameaças
└─ .github/workflows/  # CI (build desktop + android)
```

## 🚀 Começando (desenvolvimento)

### Pré-requisitos
- **Desktop:** [.NET 9 SDK](https://dotnet.microsoft.com/download) + Windows 10/11
- **Mobile:** [Android Studio](https://developer.android.com/studio) (Ladybug ou superior) + JDK 17

### Rodar o desktop
```bash
cd desktop
dotnet build ClipBridge.sln
dotnet run --project src/ClipBridge.Desktop
```

### Rodar o mobile
Abra a pasta `mobile/` no Android Studio, deixe o Gradle sincronizar e rode no emulador/dispositivo.

> ⚠️ Este repositório está na fase de **fundação** (scaffold + arquitetura). Veja o [roadmap](#-roadmap) para o que já existe e o que vem a seguir.

## 🗺️ Roadmap

- [x] Arquitetura, protocolo e modelo de segurança documentados
- [x] Scaffold do desktop (.NET 9 + WPF/Fluent) com esqueleto dos serviços
- [x] Scaffold do mobile (Kotlin + Compose/Material 3)
- [x] CI (build desktop + android)
- [ ] Handshake de pareamento (X25519 + QR) e sessão cifrada
- [ ] Sync de clipboard de texto
- [ ] Sync de clipboard de imagens
- [ ] Captura e envio de screenshots em alta resolução
- [ ] Digitação simulada (`Ctrl+F1`)
- [ ] Empacotamento/instalador (MSIX / APK assinado)

## 🔐 Segurança

Leve a sério: veja [`SECURITY.md`](SECURITY.md) para a política de reporte de vulnerabilidades e [`docs/THREAT-MODEL.md`](docs/THREAT-MODEL.md) para o modelo de ameaças.

## 🤝 Contribuindo

Confira [`CONTRIBUTING.md`](CONTRIBUTING.md). O projeto usa [Conventional Commits](https://www.conventionalcommits.org/).

## 📄 Licença

[MIT](LICENSE) © 2026 ESousa97
