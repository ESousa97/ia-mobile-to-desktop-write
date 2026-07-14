# ClipBridge — App Android

Cliente Android do ClipBridge — Kotlin + Jetpack Compose + Material 3.

## Primeiro uso

O **Gradle Wrapper** (`gradlew`, `gradlew.bat` e `gradle/wrapper/gradle-wrapper.jar`) **não é versionado** neste scaffold. Gere-o de uma destas formas:

- **Android Studio:** abra a pasta `mobile/`. O Studio sincroniza e materializa o wrapper automaticamente.
- **Linha de comando** (com Gradle instalado):
  ```bash
  cd mobile
  gradle wrapper --gradle-version 8.11.1
  ./gradlew assembleDebug
  ```

## Estrutura

```
app/src/main/java/com/esousa/clipbridge/
├─ MainActivity.kt            # Activity Compose (edge-to-edge)
├─ ClipBridgeApplication.kt
├─ ui/                        # Tela + tema Material 3 + ViewModel (UDF)
├─ protocol/                  # Modelos do protocolo (espelham o desktop)
├─ net/                       # Cliente WebSocket (OkHttp)
├─ discovery/                 # Descoberta NSD/mDNS
├─ clipboard/                 # Leitura/escrita da área de transferência
└─ security/                  # Cifra de sessão AES-256-GCM (compatível com o desktop)
```

## Requisitos

- Android Studio Ladybug (ou superior)
- JDK 17
- `minSdk 26`, `targetSdk 35`

## Permissões e por quê

| Permissão | Uso |
|---|---|
| `INTERNET`, `ACCESS_NETWORK_STATE`, `ACCESS_WIFI_STATE` | Conexão WebSocket na LAN |
| `CHANGE_WIFI_MULTICAST_STATE` | Descoberta mDNS/NSD |
| `CAMERA` | Apenas para escanear o QR de pareamento |
| `FOREGROUND_SERVICE*`, `POST_NOTIFICATIONS` | Manter a conexão viva enquanto ativo |

Nenhuma permissão de localização, contatos ou armazenamento amplo. Ver [`../docs/SECURITY-DESIGN.md`](../docs/SECURITY-DESIGN.md).
