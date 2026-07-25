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
- `WindowsKeyboardTypingService` — `SendInput` por scancode, com fallback Unicode (**somente local**)
- `Win32HotkeyService` — atalhos globais `Ctrl+F`, `Ctrl+F1` e `Ctrl+Alt+B`
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

### 4. Digitação (`Ctrl+F1` / `Ctrl+Alt+B`)

Disparada **apenas no desktop** por hotkey local — nunca por comando remoto (ver [`THREAT-MODEL.md`](THREAT-MODEL.md)).

A injeção tem três níveis, do mais fiel ao hardware para o menos:

1. **Scancode** (`KEYEVENTF_SCANCODE`) no layout da janela em foco, com os modificadores da tecla (Shift / AltGr como Alt direito estendido).
2. **Tecla morta + letra base** (`´` + `a` → `á`), para acentos sem tecla própria no layout.
3. **Unicode** (`KEYEVENTF_UNICODE`), para o que o layout não produz — emoji, por exemplo.

O nível 1 existe por causa das sessões remotas. Citrix Workspace, RDP e similares leem o teclado no nível bruto e transmitem *scancode* pelo canal do protocolo; um evento Unicode chega como `VK_PACKET`, sem scancode, e o cliente não tem o que enviar. `Ctrl+Alt+B` existe pelo mesmo motivo: `Ctrl+F1` é atalho reservado do Citrix (`Ctrl+Alt+Del` na sessão) e é consumido pelo cliente antes do `WM_HOTKEY`.

Em tela cheia o cliente captura o teclado inteiro e **nenhum** atalho global local dispara. Para esse caso existe o botão da janela, que arma a digitação em vez de contar um prazo fixo: o Beam observa o foco a cada 200 ms e digita quando ele fica 1,2 s parado em uma janela que não é do próprio processo (limite de 30 s). Um prazo fixo obrigaria o usuário a achar o campo contra o relógio e jogaria a área de transferência na janela errada quando ele não chegasse a tempo.

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
