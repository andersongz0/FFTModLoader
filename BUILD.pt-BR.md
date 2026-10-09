# Compilar FFTModLoader

[English](BUILD.md) | [Português](BUILD.pt-BR.md)

Windows x64; Visual Studio 2022 com C++/MASM e CMake 3.24+; SDK .NET compatível com net8/net9.
Execute `./build-source.ps1` para compilar launcher, RuntimeLogRelayTests, JobExpansion e ContentExpansion.
Isso não instala nada no jogo nem altera saves.

- `src/ContentExpansionApi`: contrato compartilhado.
- `src/ContentExpansionModule`: provedor integrado, compilando fontes Core/Runtime.
- `src/ContentExpansionCore`, `Runtime`, `Native`: tabelas, fórmulas, saves, rotas nativas e bridge.
- `tools/build_native_bridge.ps1`: compila o bridge; rc.1 usa o bridge v5 aprovado, idêntico byte a byte.
- `vendor/Reloaded-II`: código completo do runtime com modificações locais e licença GPL original.

Para os testes de integração, obtenha também o código de ReworkedChemist e passe
`-p:ReworkedChemistSourceDirectory="caminho/ReworkedChemist/AlchemistRework"`
ao executar `dotnet run --project tests/ContentExpansionModuleTests -c Release`.
Eles verificam exportação, provedor sem ativação automática, ativação única, dependência ausente e API incompatível.
Outros testes estão em `tests`; testes nativos exigem executável legítimo e bibliotecas de hooks locais.
Testes completos em memória isolada ficam no repositório ReworkedChemist e referenciam este módulo.

Testes sintéticos não equivalem a validação visual/GPU no jogo.
Recompilar gera binários de desenvolvimento, não uma aprovação automática pelo jogador.
Não publique executável do jogo, PAC completo, saves nem gravações privadas.
