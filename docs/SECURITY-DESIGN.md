# Design de Segurança

O ClipBridge trata a área de transferência — que frequentemente contém senhas, tokens e dados pessoais — como **material sensível**. Mesmo em rede local, o tráfego é cifrado ponta-a-ponta.

## Camadas de proteção

```
┌───────────────────────────────────────────────────────────────┐
│ 5. Higiene de segredos  → nada de chaves/keystores no Git       │
│ 4. Menor privilégio     → só as permissões necessárias          │
│ 3. Autorização          → só dispositivos pareados; código/sessão │
│ 2. Confidencialidade    → AES-256-GCM (E2E) em todo payload     │
│ 1. Confinamento de rede → bind em LAN; sem exposição à internet │
└───────────────────────────────────────────────────────────────┘
```

## 1. Confinamento de rede

- O servidor faz bind apenas nas interfaces de rede local (nunca `0.0.0.0` acessível externamente sem confinamento).
- Recomenda-se regra de firewall restrita à sub-rede (ex.: `192.168.0.0/16`, `10.0.0.0/8`).
- Sem UPnP, sem abertura de porta no roteador, sem relay na nuvem.

## 2. Confidencialidade — criptografia de sessão

- **Troca de chaves:** X25519 (ECDH) com chaves **efêmeras** por sessão → *forward secrecy*.
- **Derivação:** `HKDF-SHA256` sobre o segredo compartilhado → chaves distintas para cada direção.
- **Cifra:** `AES-256-GCM` (AEAD — confidencialidade + integridade). Nonce de 96 bits, contador monotônico por direção; a chave é rotacionada bem antes de qualquer risco de reuso de nonce.
- **Onde:** todo `payload` do protocolo e todos os `blob.chunk` são cifrados. Só metadados mínimos de framing ficam em claro.

## 3. Autenticação e autorização

### Pareamento
- O desktop exibe um **código numérico de 6 dígitos** (expira em 5 minutos, uso único).
- O celular descobre o desktop na LAN via **broadcast UDP** e envia o código em `pair.confirm` após o handshake X25519.
- Após **5 tentativas inválidas**, o convite é invalidado e um novo código deve ser gerado no desktop.
- **Limite conhecido:** o código de 6 dígitos não autentica a chave pública do desktop fora de banda (diferente do esquema anterior por QR+fingerprint). O modelo assume LAN semiconfiável; veja [`THREAT-MODEL.md`](THREAT-MODEL.md).

### Sessões e reconexão automática
- No pareamento por código, além da chave de sessão, os dois lados derivam do **mesmo segredo ECDH** uma **chave de retomada** de longa duração (`HKDF` com info distinta — `clipbridge-v1-resume`). Ela nunca trafega.
- O identificador do vínculo (`deviceId`) é `HMAC-SHA256(chaveDeRetomada, "clipbridge-v1-device-id")` truncado: os dois lados chegam ao mesmo valor sem trocá-lo, e ele não revela a chave.
- **Validade: 72 horas contadas a partir da última conexão bem-sucedida** — cada retomada renova a janela nos dois lados. Vencida, só um novo código restabelece o vínculo.
- Na reconexão, o `session.resume` traz uma chave X25519 **efêmera nova**: a chave de sessão é `HKDF(ECDH ‖ chaveDeRetomada)`. O ECDH efêmero preserva o *forward secrecy* (uma retomada gravada hoje não é decifrável nem com a chave de retomada vazada amanhã) e a chave de retomada autentica os dois lados, com provas HMAC em ambas as direções.
- **Em repouso:** no Windows, DPAPI com escopo do usuário atual (`%LOCALAPPDATA%\Beam\trusted-devices.bin` — ilegível em outra conta ou máquina); no Android, AES-GCM com chave do AndroidKeyStore, fora do backup.
- **Revogação:** o botão "Revogar dispositivos" no desktop apaga todos os vínculos e derruba as sessões em curso; a próxima retomada é recusada com `resume.denied` e o celular apaga a cópia local.
- Rate limiting por sessão para mitigar flood/abuso.

## 4. Menor privilégio

| Plataforma | Permissão | Uso |
|---|---|---|
| Android | `INTERNET` | Conexão WebSocket na LAN |
| Android | `POST_NOTIFICATIONS` | Notificação do serviço em foreground |
| Windows | Sem elevação | Hotkeys, clipboard, captura e `SendInput` não exigem admin |

Nenhuma permissão de localização, contatos, armazenamento amplo, etc.

## 5. Higiene de segredos

- Chaves privadas, keystores e certificados **nunca** entram no Git — ver [`.gitignore`](../.gitignore).
- No Android, a keystore de assinatura fica fora do repo; credenciais via `keystore.properties` (ignorado) ou secrets do CI.
- No Windows, o material de chave persistido é protegido por **DPAPI** (escopo do usuário) no armazenamento local do app.
- CI usa **GitHub Secrets**; nenhum segredo em logs.

## Considerações e limites conhecidos (fase atual)

- O handshake implementa X25519 efêmero, HKDF-SHA256, código de pareamento de uso único com limite de tentativas. A sessão só libera mensagens após o `ack` da confirmação.
- Recomenda-se auditoria independente da camada de cripto antes de qualquer release estável.
- O modelo assume uma LAN semiconfiável; para redes hostis (Wi-Fi público), o confinamento por firmware/firewall é essencial.

Veja também o [`THREAT-MODEL.md`](THREAT-MODEL.md).
