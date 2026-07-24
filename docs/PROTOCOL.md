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
| `session.resume` | mobile→desktop | `{ deviceId, pubKey, nonce }` | Retoma um vínculo anterior, sem código |
| `session.resumed` | desktop→mobile | `{ pubKey, nonce, proof }` | Aceita a retomada e prova conhecer a chave de retomada |
| `session.resume.confirm` | mobile→desktop | `{ proof }` *(cifrado)* | Prova do celular; conclui a retomada |
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
DISCONNECTED → CONNECTING → HELLO → PAIRING  → SECURE ⇄ (mensagens) → CLOSED
                                  └→ RESUMING ↗   │
                                        │         │
                                   (falha) → ERROR → DISCONNECTED
```

- Mensagens de dados (`clipboard.*`, `screenshot`, `blob.*`) só são aceitas no estado **SECURE**.
- Heartbeat (`ping`/`pong`) a cada 15s; 3 falhas → reconecta.
- Com vínculo válido o celular entra por **RESUMING** (sem código); sem vínculo, por **PAIRING**.

## Retomada de sessão (reconexão automática)

Concluído o pareamento por código, os dois lados derivam do mesmo segredo ECDH — com info HKDF distinta (`clipbridge-v1-resume`) — uma **chave de retomada** que nunca trafega, e a persistem. O `deviceId` que a identifica é `HMAC-SHA256(chaveDeRetomada, "clipbridge-v1-device-id")` truncado em 8 bytes (hex), calculado independentemente pelos dois lados.

```
mobile → session.resume         { deviceId, pubKey efêmera, nonceC }
desktop→ session.resumed        { pubKey efêmera, nonceS, proof = HMAC(resume, "…server" ‖ salt) }
mobile → session.resume.confirm { proof = HMAC(resume, "…client" ‖ salt) }   ← cifrado
desktop→ ack                    { ackId }
```

- `salt = nonceC ‖ nonceS`; ambos os nonces têm 32 bytes.
- Chave da sessão retomada: `HKDF-SHA256(ikm = ECDH ‖ chaveDeRetomada, salt, info = "clipbridge-v1-resume-session")`. O ECDH efêmero preserva o sigilo futuro; a chave de retomada autentica.
- **Validade: 72h a partir da última conexão bem-sucedida**, renovada nos dois lados a cada retomada. Vínculo desconhecido, vencido ou revogado → `error { code: "resume.denied" }`, e o celular apaga a cópia local e volta a pedir o código.
- O celular tenta reconectar sozinho com backoff de 2s, 4s, 8s, 16s e depois a cada 30s, reagindo também ao retorno da rede; usa o IP recém-descoberto quando o desktop mudou de endereço.

## Descoberta e pareamento

A descoberta usa duas vias complementares:

- **Ativa** — o mobile envia `clipbridge.discover.v1` por UDP na porta `8788` para o broadcast dirigido de cada interface (e para `255.255.255.255`), retransmitindo a cada 2s no primeiro minuto e a cada 10s depois; o desktop responde por unicast com `clipbridge.announce.v1:{porta-websocket};`.
- **Passiva** — o desktop emite `clipbridge.announce.v1:{porta-websocket};` em broadcast a cada 2s na porta UDP `8789`; o mobile escuta essa porta enquanto a descoberta estiver ativa. Isso permite a descoberta mesmo quando o firewall do Windows bloqueia o UDP de entrada na porta `8788` (perfil de rede "Pública", comum em Wi-Fi).

> **O `;` final é obrigatório.** O receptor casa a mensagem inteira (`^…;$`) e descarta o que não terminar assim. Sem esse terminador, um datagrama cortado no meio dos dígitos — `…:8787` lido como `…:878` — passaria como uma porta sintaticamente válida, e o celular tentaria conectar onde ninguém escuta. Pela mesma razão o mobile restaura `DatagramPacket.length` antes de cada `receive`: no Android o `length` remanescente do datagrama anterior limita a leitura seguinte.

A busca roda continuamente enquanto não há sessão segura — o desktop pode entrar na rede depois do celular — e é suspensa assim que o estado `SECURE` é alcançado. O mobile mantém um `MulticastLock` durante a busca (sem ele o Android descarta broadcasts recebidos) e um `WifiLock` durante a sessão (sem ele o rádio Wi-Fi dorme com a tela apagada e derruba o WebSocket).

Em builds de debug do Android, o emulador também consulta `10.0.2.2` (alias da máquina host).

> **Firewall:** o Windows precisa permitir a entrada em TCP `8787` (WebSocket) e UDP `8788`/`8789` (descoberta). O card "Acesso pela Wi-Fi" na janela do desktop verifica e cria as regras sob confirmação do UAC. Equivalente manual, num terminal como administrador:
>
> ```
> netsh advfirewall firewall add rule name="Beam (TCP 8787)" dir=in action=allow protocol=TCP localport=8787 profile=any
> netsh advfirewall firewall add rule name="Beam descoberta (UDP 8788)" dir=in action=allow protocol=UDP localport=8788,8789 profile=any
> ```

**Conexão manual** — em redes com isolamento de clientes (AP isolation) ou broadcast filtrado, a descoberta não passa mas o WebSocket sim. O desktop exibe seus endereços `ip:porta`; no mobile, "Informar IP manualmente" aceita `192.168.0.10` ou `192.168.0.10:8787` e pareia direto.

O desktop gera um código numérico aleatório de seis dígitos, expira em cinco minutos e só pode ser usado uma vez. Após **cinco tentativas inválidas**, o convite é invalidado. O mobile envia o código em `pair.confirm` depois do handshake de chave efêmera. O desktop só envia `ack` depois de validar o código; ambos os lados então passam ao estado `SECURE`. Mensagens de aplicação recebidas antes desse estado retornam `error { code: "auth.failed" }`.

## Versionamento

O campo `v` permite evolução. Um par negocia a maior versão comum no `hello`. Mudanças incompatíveis incrementam `v`; campos novos e opcionais não o fazem.

## Códigos de erro

| `code` | Significado |
|---|---|
| `auth.failed` | Handshake/código inválido ou convite expirado |
| `resume.denied` | Vínculo desconhecido, expirado, revogado ou prova inválida |
| `blob.checksum` | SHA-256 do blob não confere |
| `blob.toolarge` | Blob acima do limite configurado |
| `proto.unsupported` | Versão de protocolo incompatível |
| `rate.limited` | Excesso de mensagens (proteção anti-flood) |
