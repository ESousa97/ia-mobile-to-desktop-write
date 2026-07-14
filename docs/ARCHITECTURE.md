# Arquitetura do ClipBridge

## Visão geral

O ClipBridge sincroniza a área de transferência entre um **PC Windows** e um **celular Android** dentro da mesma rede local, e adiciona dois recursos exclusivos do desktop: captura de tela em alta resolução e digitação simulada do conteúdo copiado.

O princípio de design central é **local-first**: nenhum dado sai da LAN. Não há servidor na nuvem, contas ou terceiros no caminho.

## Papéis: servidor e cliente

| | Papel | Justificativa |
|---|---|---|
| **Desktop (Windows)** | **Servidor** | Fica ligado, tem IP relativamente estável na LAN, e é onde vivem os recursos de tela/teclado. Publica um serviço mDNS e aceita conexões WebSocket. |
| **Mobile (Android)** | **Cliente** | Descobre o desktop via mDNS, faz o pareamento e conecta. Reconecta automaticamente quando volta à rede. |

Um servidor pode aceitar múltiplos dispositivos pareados (ex.: celular + tablet).

## Componentes

### Desktop (`desktop/src`)

```
ClipBridge.Desktop  (WPF, net9.0-windows)      ← UI + serviços específicos do Windows
        │
        ├── depende de ──►  ClipBridge.Core (net9.0)   ← lógica agnóstica de plataforma
        │
ClipBridge.Core.Tests (xUnit)                  ← testes de protocolo/cripto
```

**ClipBridge.Core** (reutilizável, sem dependência de UI):
- `Protocol/` — modelos das mensagens (envelope, tipos, serialização JSON)
- `Net/` — servidor WebSocket, gerenciamento de sessões, framing
- `Discovery/` — anúncio mDNS (`_clipbridge._tcp`)
- `Security/` — handshake X25519, HKDF, cifra AES-256-GCM, gestão de dispositivos pareados
- `Abstractions/` — interfaces para os serviços de plataforma (`IClipboardService`, `IScreenCaptureService`, `IKeyboardTypingService`, `IHotkeyService`)

**ClipBridge.Desktop** (WPF + WPF-UI, específico do Windows):
- Implementações Win32 das abstrações:
  - `WindowsClipboardService` — lê/escreve texto e imagens (`System.Windows.Clipboard`, STA)
  - `WindowsScreenCaptureService` — captura full-res via GDI `BitBlt` → PNG
  - `WindowsKeyboardTypingService` — `SendInput` (Unicode) para digitar caractere a caractere
  - `Win32HotkeyService` — `RegisterHotKey` para atalhos globais (`Ctrl+F`, `Ctrl+F1`)
- UI: janela principal (status, dispositivos pareados, QR de pareamento), ícone na bandeja (H.NotifyIcon), início minimizado

### Mobile (`mobile/app`)

Arquitetura **MVVM + Unidirectional Data Flow** (padrão recomendado para Compose):

```
UI (Composables, Material 3)
   │  observa StateFlow
ViewModel
   │  usa
Repositórios / Serviços
   ├── ClipBridgeClient       (WebSocket — OkHttp/Ktor)
   ├── DiscoveryService       (NSD / mDNS)
   ├── ClipboardManager       (ClipboardManager do Android)
   ├── PairingManager         (QR scan + X25519 + AES-GCM)
   └── ScreenshotStore        (cache das capturas recebidas)
```

Serviço em foreground para manter a conexão viva enquanto ativo, respeitando as restrições de background do Android.

## Fluxos principais

### 1. Descoberta + pareamento (primeira vez)
```
Desktop                                   Mobile
  │  publica _clipbridge._tcp (mDNS)         │
  │  exibe QR (host, porta, chave pública,   │
  │           fingerprint, token de par.)    │
  │                                          │  usuário escaneia o QR
  │  ◄──────── conecta (WebSocket) ──────────│
  │  ◄──────── handshake X25519 ────────────►│  deriva chave de sessão (HKDF)
  │  ◄──────── verifica fingerprint ────────►│  (proteção contra MITM)
  │  ────────── sessão cifrada pronta ──────►│
```

### 2. Copiar no PC → aparece no celular
```
Clipboard watcher (Win) detecta mudança
  → normaliza (texto UTF-8 / imagem PNG)
  → serializa envelope → cifra (AES-256-GCM)
  → envia via WebSocket para cada sessão pareada
Mobile recebe → decifra → grava no ClipboardManager / mostra a imagem
```

### 3. `Ctrl+F` — screenshot em alta resolução
```
Hotkey global dispara → captura full-res (todos os monitores ou o principal)
  → PNG sem perda → (opcional) fragmentação em chunks
  → envia cifrado → Mobile exibe em visualizador com zoom/pan
```

### 4. `Ctrl+F1` — digitar o texto copiado
```
Hotkey global dispara → lê o texto atual do clipboard
  → SendInput injeta cada caractere Unicode na janela em foco
  (útil onde Ctrl+V é bloqueado; NÃO usa a área de transferência do destino)
```

## Decisões de arquitetura (ADR resumido)

| Decisão | Escolha | Alternativas descartadas | Motivo |
|---|---|---|---|
| Transporte | WebSocket sobre TCP na LAN | UDP puro, gRPC | Bidirecional, simples, ótimo suporte nas duas plataformas |
| Descoberta | mDNS / NSD | Broadcast UDP manual, digitar IP | Padrão, zero-config, suportado nativamente |
| Cripto | AES-256-GCM + X25519/HKDF (camada de app) | TLS com cert autoassinado | E2E real, pinning simples via fingerprint, sem PKI |
| Desktop | .NET 9 + WPF + WPF-UI | Electron, WinUI 3, Tauri | Nativo, leve, acesso limpo a Win32 (hotkey/SendInput/captura) |
| Mobile | Kotlin + Compose + Material 3 | Flutter, React Native | Padrão nativo Android, UI polida, ícones não-inline |
| Fragmentação | Chunks binários para imagens grandes | Base64 em JSON | Evita inflar 33% e picos de memória |

## Portas e rede

- Porta padrão do servidor: **`8787`** (configurável). Bind restrito às interfaces de LAN.
- Serviço mDNS: `_clipbridge._tcp.local`.
- Nenhuma porta é exposta à internet; recomenda-se regra de firewall limitada à sub-rede local.

Veja o formato das mensagens em [`PROTOCOL.md`](PROTOCOL.md) e o modelo de segurança em [`SECURITY-DESIGN.md`](SECURITY-DESIGN.md).
