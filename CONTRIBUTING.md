# Contribuindo com o ClipBridge

Obrigado pelo interesse! Este guia mantém o histórico limpo e o código consistente.

## Fluxo de trabalho

1. Crie uma branch a partir de `main`:
   - `feat/<descrição-curta>` para novas funcionalidades
   - `fix/<descrição-curta>` para correções
   - `docs/<descrição-curta>` para documentação
2. Faça commits pequenos e coesos (ver convenção abaixo).
3. Garanta que o build e os testes passam localmente.
4. Abra um Pull Request descrevendo o **porquê** da mudança, não só o **o quê**.

## Conventional Commits

Usamos [Conventional Commits](https://www.conventionalcommits.org/):

```
<tipo>(<escopo opcional>): <descrição no imperativo>
```

Tipos: `feat`, `fix`, `docs`, `chore`, `refactor`, `test`, `ci`, `build`, `perf`, `style`.

Escopos comuns: `desktop`, `mobile`, `core`, `protocol`, `security`, `ci`.

Exemplos:
- `feat(desktop): register global hotkey for screen capture`
- `fix(core): handle partial websocket frames on slow networks`
- `docs(protocol): document image chunking format`

## Padrões de código

- **C# / .NET:** siga o [`.editorconfig`](.editorconfig). Rode `dotnet format` antes de commitar.
- **Kotlin:** siga o Kotlin style guide + Compose guidelines. Sem wildcard imports.
- **Sem segredos no código.** Configurações sensíveis vêm de arquivos ignorados (ver [`.gitignore`](.gitignore)).

## Testes

- Desktop: `dotnet test` na pasta `desktop/`.
- Mobile: testes unitários via Gradle (`./gradlew test`).

## Reportando bugs

Abra uma issue com passos de reprodução, comportamento esperado x obtido, versão do SO e logs relevantes (sem dados sensíveis).
