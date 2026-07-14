# ClipBridge — App Android

Cliente Android do ClipBridge — Kotlin + Jetpack Compose + Material 3.

## Primeiro uso

O **Gradle Wrapper** (`gradlew`, `gradlew.bat` e `gradle/wrapper/gradle-wrapper.jar`) está versionado. Para buildar:

```bash
cd mobile
./gradlew assembleDebug
```

No Windows:

```bat
cd mobile
gradlew.bat assembleDebug
```

## Estrutura

```
app/src/main/java/com/esousa/clipbridge/
├─ MainActivity.kt            # Activity Compose (edge-to-edge)
├─ ClipBridgeApplication.kt
├─ ui/                        # Tela + tema Material 3 + ViewModel (UDF)
├─ protocol/                  # Modelos do protocolo (espelham o desktop)
├─ net/                       # Cliente WebSocket (OkHttp)
├─ discovery/                 # Descoberta UDP na LAN
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
| `INTERNET`, `ACCESS_NETWORK_STATE`, `ACCESS_WIFI_STATE` | Conexão WebSocket e descoberta UDP na LAN |
| `FOREGROUND_SERVICE*`, `POST_NOTIFICATIONS` | Manter a conexão viva enquanto ativo |

Nenhuma permissão de câmera, localização, contatos ou armazenamento amplo. Ver [`../docs/SECURITY-DESIGN.md`](../docs/SECURITY-DESIGN.md).
