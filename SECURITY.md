# Política de Segurança

## Versões suportadas

O ClipBridge está em desenvolvimento inicial. Correções de segurança são aplicadas apenas ao branch `main`.

## Reportando uma vulnerabilidade

**Não abra uma issue pública para vulnerabilidades de segurança.**

Prefira o canal privado do GitHub:

1. Acesse a aba **Security** do repositório → **Report a vulnerability** (GitHub Private Vulnerability Reporting).
2. Descreva o problema, o impacto e, se possível, uma prova de conceito.

Retorno esperado em até **72 horas**. Pedimos um prazo razoável de divulgação coordenada (90 dias) antes de tornar público.

## Escopo

Estão dentro do escopo, entre outros:

- Bypass do handshake de pareamento / autenticação
- Quebra ou enfraquecimento da criptografia de sessão (AES-256-GCM / X25519)
- Vazamento de conteúdo da área de transferência para fora da sessão pareada
- Exposição não intencional do servidor para fora da rede local (ex.: bind em `0.0.0.0` sem confinamento)
- Injeção via conteúdo recebido (ex.: payload malicioso que dispara digitação/execução no host)

## Princípios de segurança do projeto

- **Local-first:** por padrão, nada trafega fora da LAN.
- **Zero-trust na rede:** mesmo em LAN, todo o tráfego é cifrado ponta-a-ponta.
- **Menor privilégio:** o app pede apenas as permissões estritamente necessárias.
- **Segredos fora do versionamento:** chaves, certificados e keystores nunca entram no Git (ver [`.gitignore`](.gitignore)).

Detalhes de design em [`docs/SECURITY-DESIGN.md`](docs/SECURITY-DESIGN.md) e [`docs/THREAT-MODEL.md`](docs/THREAT-MODEL.md).
