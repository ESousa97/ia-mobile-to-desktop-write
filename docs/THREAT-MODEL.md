# Modelo de Ameaças

Modelo simplificado no estilo STRIDE para o ClipBridge, focado na fase local-first.

## Ativos a proteger

- Conteúdo da área de transferência (texto/imagens — pode conter senhas, tokens, dados pessoais)
- Screenshots capturados
- Chaves criptográficas e material de pareamento
- Integridade do host (evitar que conteúdo recebido dispare ações indevidas)

## Superfície de ataque

- Servidor WebSocket na LAN (porta 8787)
- Descoberta UDP na LAN (porta 8788)
- Código de pareamento de 6 dígitos exibido no desktop
- Conteúdo recebido do par (texto/imagem/comandos)

## Ameaças e mitigações (STRIDE)

| Categoria | Ameaça | Mitigação |
|---|---|---|
| **S**poofing | Dispositivo malicioso finge ser o par pareado | Chaves por dispositivo (TOFU), código de pareamento de uso único, limite de 5 tentativas |
| **T**ampering | Alteração de mensagens em trânsito | AEAD (AES-256-GCM) garante integridade; qualquer adulteração falha na verificação |
| **R**epudiation | Ausência de rastro de eventos | Log local de conexões/pareamentos (sem conteúdo sensível) |
| **I**nformation Disclosure | Sniffing na LAN (Wi-Fi) | Criptografia E2E; nada trafega em claro além de framing mínimo |
| **D**enial of Service | Flood de mensagens/conexões | Rate limiting por sessão, limite de tamanho de blob, timeouts |
| **E**levation of Privilege | Payload recebido executa/injeta no host | `Ctrl+F1` (digitação) é **sempre iniciado pelo usuário local**, nunca por comando remoto; conteúdo recebido é tratado como dado, nunca como comando |

## Fronteiras de confiança

```
   Internet (NÃO confiável)  ──✗── (sem exposição; sem relay)
        │
   Roteador / Firewall
        │
   LAN (semiconfiável)  ──── tráfego cifrado E2E ────
        │
   Dispositivos pareados (confiáveis após código + handshake)
```

## Limites conhecidos do pareamento por código

- O código de 6 dígitos (~20 bits de entropia) é transmitido em `ws://` antes da sessão cifrada; um atacante na mesma LAN pode tentar força bruta dentro da janela de 5 minutos (mitigado por expiração, uso único e bloqueio após 5 tentativas).
- A descoberta UDP responde a qualquer `clipbridge.discover.v1` na LAN; um atacante pode anunciar um servidor falso. O mobile conecta ao primeiro respondedor — não há verificação de identidade do host antes do pareamento.
- Para redes hostis, recomenda-se confinamento por firewall e evolução futura para PAKE (ex.: SPAKE2) ou retorno a verificação out-of-band (QR + fingerprint).

## Decisões de segurança que reduzem risco por design

- **Sem nuvem:** elimina toda uma classe de ameaças (conta comprometida, servidor invadido, retenção de dados por terceiros).
- **Digitação nunca é remota:** o recurso de "digitar o texto copiado" é disparado por hotkey **local**; um par remoto não consegue forçar o host a digitar/executar nada.
- **Efemeridade de chaves:** forward secrecy limita o impacto de uma chave comprometida.
- **Revogação simples:** o usuário remove um dispositivo pareado na UI e as chaves são invalidadas.

## Fora de escopo (nesta fase)

- Host já comprometido por malware com privilégios (fora do modelo).
- Ataques físicos ao dispositivo desbloqueado.
- Análise de canal lateral na implementação de cripto (mitigado usando primitivas de biblioteca revisadas, não implementações caseiras).
