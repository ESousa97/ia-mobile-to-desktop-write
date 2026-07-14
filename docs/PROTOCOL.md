# Protocolo ClipBridge (v1)

Protocolo de aplicação sobre **WebSocket**. Mensagens de controle são **JSON (texto)**; grandes blobs (imagens) usam **frames binários** referenciados por id.

Após o handshake, **todo payload é cifrado com AES-256-GCM** (ver [`SECURITY-DESIGN.md`](SECURITY-DESIGN.md)). O envelope abaixo descreve o payload em claro (o que existe *dentro* da cifra).

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
| `pair.response` | desktop→mobile | `{ pubKey, fingerprint }` | Resposta do handshake + fingerprint p/ verificação |
| `pair.confirm` | mobile→desktop | `{ token }` | Confirma o pareamento com o token do QR |
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

## QR de pareamento

O desktop gera um convite efêmero no formato URI abaixo e o codifica como QR Code:

```text
clipbridge://pair?host={host}&port={port}&pubKey={base64}&fingerprint={sha256-12-hex}&token={base64}&expiresAt={epoch-millis}
```

O mobile valida o `fingerprint` recebido em `pair.response` contra o valor do QR antes de enviar `pair.confirm`. O `token` tem 32 bytes aleatórios, expira em cinco minutos e é aceito somente uma vez. O desktop só envia `ack` depois de validar o token; ambos os lados então passam ao estado `SECURE`. Mensagens de aplicação recebidas antes desse estado retornam `error { code: "auth.failed" }`.

## Versionamento

O campo `v` permite evolução. Um par negocia a maior versão comum no `hello`. Mudanças incompatíveis incrementam `v`; campos novos e opcionais não o fazem.

## Códigos de erro

| `code` | Significado |
|---|---|
| `auth.failed` | Handshake/token inválido |
| `auth.fingerprint` | Fingerprint não confere (possível MITM) |
| `blob.checksum` | SHA-256 do blob não confere |
| `blob.toolarge` | Blob acima do limite configurado |
| `proto.unsupported` | Versão de protocolo incompatível |
| `rate.limited` | Excesso de mensagens (proteção anti-flood) |
