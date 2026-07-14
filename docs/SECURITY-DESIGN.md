# Design de Segurança

O ClipBridge trata a área de transferência — que frequentemente contém senhas, tokens e dados pessoais — como **material sensível**. Mesmo em rede local, o tráfego é cifrado ponta-a-ponta.

## Camadas de proteção

```
┌───────────────────────────────────────────────────────────────┐
│ 5. Higiene de segredos  → nada de chaves/keystores no Git       │
│ 4. Menor privilégio     → só as permissões necessárias          │
│ 3. Autorização          → só dispositivos pareados; token/sessão│
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
- O desktop exibe um **QR Code** contendo: `host`, `porta`, **chave pública**, **fingerprint** e um **token de pareamento** de uso único (expira em minutos).
- O celular escaneia, executa o handshake e confirma com o token.
- O **fingerprint** (hash da chave pública, exibido também em texto) permite verificação visual anti-**MITM**: os dois lados mostram o mesmo código curto.

### Sessões
- Após pareado, o dispositivo guarda a chave pública do par (**TOFU** — trust on first use) e um identificador de dispositivo.
- Reconexões usam as chaves persistidas; um par pode ser **revogado** a qualquer momento na UI do desktop.
- Rate limiting por sessão para mitigar flood/abuso.

## 4. Menor privilégio

| Plataforma | Permissão | Uso |
|---|---|---|
| Android | `INTERNET` | Conexão WebSocket na LAN |
| Android | `CAMERA` | Apenas para escanear o QR de pareamento |
| Android | `POST_NOTIFICATIONS` | Notificação do serviço em foreground |
| Windows | Sem elevação | Hotkeys, clipboard, captura e `SendInput` não exigem admin |

Nenhuma permissão de localização, contatos, armazenamento amplo, etc.

## 5. Higiene de segredos

- Chaves privadas, keystores e certificados **nunca** entram no Git — ver [`.gitignore`](../.gitignore).
- No Android, a keystore de assinatura fica fora do repo; credenciais via `keystore.properties` (ignorado) ou secrets do CI.
- No Windows, o material de chave persistido é protegido por **DPAPI** (escopo do usuário) no armazenamento local do app.
- CI usa **GitHub Secrets**; nenhum segredo em logs.

## Considerações e limites conhecidos (fase atual)

- O scaffold define as **interfaces e o design**; a implementação criptográfica completa é parte do roadmap e deve passar por revisão antes de uso em produção.
- Recomenda-se auditoria independente da camada de cripto antes de qualquer release estável.
- O modelo assume uma LAN semiconfiável; para redes hostis (Wi-Fi público), o confinamento por firmware/firewall é essencial.

Veja também o [`THREAT-MODEL.md`](THREAT-MODEL.md).
