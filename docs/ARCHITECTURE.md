# Arquitetura do ClipBridge

## Visão geral

O ClipBridge sincroniza a área de transferência entre um **PC Windows** e um **celular Android** dentro da mesma rede local, e adiciona dois recursos exclusivos do desktop: captura de tela em alta resolução e digitação simulada do conteúdo copiado.

O princípio de design central é **local-first**: nenhum dado sai da LAN. Não há servidor na nuvem, contas ou terceiros no caminho.

## Papéis: servidor e cliente

| | Papel | Justificativa |
|---|---|---|
| **Desktop (Windows)** | **Servidor** | Fica ligado, IP estável na LAN, recursos de tela/teclado. Responde descoberta UDP e aceita WebSocket. |
| **Mobile (Android)** | **Cliente** | Descobre o desktop via broadcast UDP, pareia com código de 6 dígitos e mantém sync em foreground service. |

Um servidor pode aceitar múltiplos dispositivos pareados (ex.: celular + tablet).

## Componentes

### Desktop (`desktop/src`)

```
ClipBridge.Desktop  (WPF, net9.0-windows)      ← UI + serviços Windows + bandeja
        │
        ├── depende de ──►  ClipBridge.Core (net9.0)   ← protocolo, rede, cripto
        │
ClipBridge.Core.Tests (xUnit)
```

**ClipBridge.Core** (agnóstico de UI):
- `Protocol/` — envelopes JSON, payloads, `SecureEnvelopeCodec`
- `Net/` — `ClipBridgeServer`, blobs (`BlobSender`/`BlobReceiver`), UDP discovery na porta 8788
- `Security/` — X25519, HKDF, AES-256-GCM, `PairingCoordinator` (código 6 dígitos)
- `Abstractions/` — clipboard, captura, digitação, hotkeys

**ClipBridge.Desktop** (WPF + WPF-UI):
- `WindowsClipboardService` — texto e PNG via `System.Windows.Clipboard`
- `WindowsScreenCaptureService` — GDI `BitBlt` → PNG full-res
- `WindowsKeyboardTypingService` — `SendInput` Unicode (`Ctrl+F1`, **somente local**)
- `Win32HotkeyService` — atalhos globais `Ctrl+F` / `Ctrl+F1`
- `TrayIconService` — minimiza para bandeja; servidor continua ativo
- UI — código de pareamento, status, log de atividade

### Mobile (`mobile/app`)

Arquitetura **MVVM + UDF** (Compose / Material 3):

```
HomeScreen (Composables)
   │  observa StateFlow
ClipBridgeViewModel
   │  delega para
ClipBridgeSession (Application)     ← longa duração
   ├── UdpDiscovery                 (broadcast UDP :8788)
   ├── ClipBridgeClient             (WebSocket + blobs + ping/pong)
   ├── ClipboardRepository
   └── ClipBridgeForegroundService  (notificação enquanto pareado)
```

## Fluxos principais

### 1. Descoberta + pareamento (primeira vez)

```
Desktop                                   Mobile
  │  escuta UDP :8788                        │
  │  exibe código 6 dígitos                  │  broadcast clipbridge.discover.v1
  │  ◄──────── announce + porta WS ──────────│
  │  ◄──────── WebSocket + pair.request ─────│
  │  ────────── pair.response ───────────────►│
  │  ◄──────── pair.confirm { code } ────────│  (código exibido no desktop)
  │  ────────── ack ─────────────────────────►│  → estado SECURE
```

### 2. Sync de clipboard (texto / imagem)

```
Mudança local → envelope cifrado (ou blob.begin/chunk/end + metadados)
  → WebSocket → par decifra → grava clipboard / exibe preview com zoom
```

Anti-eco: flags `_suppressNextChange` (desktop) e `suppressClipboardSend` (mobile).

### 3. Screenshot (`Ctrl+F`)

```
Hotkey → captura PNG full-res → blob cifrado → screenshot { blobId, … }
  → mobile exibe no visualizador com zoom/pan
```

### 4. Digitação (`Ctrl+F1`)

Disparada **apenas no desktop** por hotkey local — nunca por comando remoto (ver [`THREAT-MODEL.md`](THREAT-MODEL.md)).

## Decisões de arquitetura (ADR resumido)

| Decisão | Escolha | Alternativas descartadas | Motivo |
|---|---|---|---|
| Transporte | WebSocket TCP | UDP puro, gRPC | Bidirecional, suporte maduro |
| Descoberta | Broadcast UDP :8788 | mDNS/NSD, IP manual | Simples, funciona no emulador (10.0.2.2 debug) |
| Pareamento | Código 6 dígitos + X25519 | QR + fingerprint | UX mais simples; limites documentados |
| Cripto | AES-256-GCM + X25519/HKDF | TLS autoassinado | E2E na camada de app |
| Blobs | Chunks binários cifrados | Base64 em JSON | Memória e overhead |
| Desktop | .NET 9 + WPF | Electron, WinUI | Win32 nativo, leve |
| Mobile | Kotlin + Compose | Flutter | Material 3 nativo |
| Background | Foreground service | WorkManager | Mantém WebSocket ativo |

## Portas e rede

- WebSocket: **`8787`** (padrão)
- Descoberta UDP: **`8788`**
- Bind desktop: `http://+:8787/` (pode exigir `urlacl` / admin)
- Sem exposição à internet; firewall restrito à sub-rede LAN

Veja [`PROTOCOL.md`](PROTOCOL.md) e [`SECURITY-DESIGN.md`](SECURITY-DESIGN.md).
