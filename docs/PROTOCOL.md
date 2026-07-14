# Protocolo ClipBridge (v1)

Protocolo de aplicação sobre **WebSocket**. Mensagens de controle são **JSON (texto)**; grandes blobs (imagens) usam **frames binários** referenciados por id.

Após o handshake, **todo payload é cifrado com AES-256-GCM** (ver [`SECURITY-DESIGN.md`](SECURITY-DESIGN.md)). O envelope abaixo descreve o payload em claro (o que existe *dentro* da cifra). No fio, o campo `payload` trafega como `{ "ct": "<base64 do pacote GCM>" }`; metadados (`v`, `type`, `id`, `ts`) ficam em claro. AAD = `type|id`.

## Envelope

```jsonc
{
  "v": 1,                    // versão do protocolo
  "type": "clipboard.text",  // tipo da mensagem (ver tabela)
  "id": "b3f1...",           // UUID da mensagem (idempotência/ack)
  "ts": 1752480000000,       // epoch millis (origem)
  "payload": { }             // específico do tipo
}
```

## Tipos de mensagem

| `type` | Direção | Payload | Descrição |
|---|---|---|---|
| `hello` | ambos | `{ device, platform, appVersion }` | Apresentação inicial pós-conexão |
| `pair.request` | mobile→desktop | `{ pubKey, nonce }` | Início do handshake (chave pública efêmera) |
| `pair.response` | desktop→mobile | `{ pubKey }` | Resposta do handshake |
| `pair.confirm` | mobile→desktop | `{ code }` | Confirma o pareamento com o código exibido no desktop |
| `clipboard.text` | ambos | `{ text, mime }` | Novo texto na área de transferência |
| `clipboard.image` | ambos | `{ blobId, mime, width, height, bytes }` | Imagem (metadados; bytes via frames binários) |
| `screenshot` | desktop→mobile | `{ blobId, mime, width, height, monitors }` | Captura de tela em alta resolução |
| `blob.begin` | ambos | `{ blobId, totalBytes, chunkSize, sha256 }` | Inicia transferência de blob grande |
| `blob.chunk` | ambos | *(frame binário)* | Um pedaço; cabeçalho binário: `blobId(16) seq(4) len(4)` + dados |
| `blob.end` | ambos | `{ blobId }` | Fim; receptor valida `sha256` |
| `type.text` | interno (desktop) | `{ text }` | Solicita digitação simulada (uso local do `Ctrl+F1`) |
| `ack` | ambos | `{ ackId }` | Confirma recebimento de uma mensagem |
| `error` | ambos | `{ code, message }` | Erro (código estável, mensagem legível) |
| `ping` / `pong` | ambos | `{ }` | Keep-alive (heartbeat) |

## Fragmentação de blobs (imagens/screenshots)

Imagens são enviadas fora do JSON para evitar o overhead de Base64 (~33%) e picos de memória:

```
blob.begin  { blobId, totalBytes, chunkSize: 65536, sha256 }
blob.chunk  [binário] blobId(16 bytes) | seq(uint32 BE) | len(uint32 BE) | dados...
blob.chunk  ...
blob.end    { blobId }
```

O receptor:
1. Aloca/streama o blob conforme os chunks chegam (em ordem via `seq`).
2. Ao receber `blob.end`, calcula o SHA-256 e compara com o anunciado.
3. Em caso de divergência → `error { code: "blob.checksum" }` e descarta.

## Máquina de estados da conexão

```
DISCONNECTED → CONNECTING → HELLO → PAIRING → SECURE ⇄ (mensagens) → CLOSED
                                        │
                                   (falha) → ERROR → DISCONNECTED
```

- Mensagens de dados (`clipboard.*`, `screenshot`, `blob.*`) só são aceitas no estado **SECURE**.
- Heartbeat (`ping`/`pong`) a cada 15s; 3 falhas → reconecta.

## Descoberta e pareamento

O mobile envia `clipbridge.discover.v1` por UDP na porta `8788`; o desktop responde com `clipbridge.announce.v1:{porta-websocket}`. Em builds de debug do Android, o emulador também consulta `10.0.2.2` (alias da máquina host).

O desktop gera um código numérico aleatório de seis dígitos, expira em cinco minutos e só pode ser usado uma vez. Após **cinco tentativas inválidas**, o convite é invalidado. O mobile envia o código em `pair.confirm` depois do handshake de chave efêmera. O desktop só envia `ack` depois de validar o código; ambos os lados então passam ao estado `SECURE`. Mensagens de aplicação recebidas antes desse estado retornam `error { code: "auth.failed" }`.

## Versionamento

O campo `v` permite evolução. Um par negocia a maior versão comum no `hello`. Mudanças incompatíveis incrementam `v`; campos novos e opcionais não o fazem.

## Códigos de erro

| `code` | Significado |
|---|---|
| `auth.failed` | Handshake/código inválido ou convite expirado |
| `blob.checksum` | SHA-256 do blob não confere |
| `blob.toolarge` | Blob acima do limite configurado |
| `proto.unsupported` | Versão de protocolo incompatível |
| `rate.limited` | Excesso de mensagens (proteção anti-flood) |
