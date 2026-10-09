# Créditos e ferramentas de desenvolvimento

[English](CREDITS.md) | [Português](CREDITS.pt-BR.md)

Integração original dos mods e loader: **ZeroDS**. Autoria de terceiros preservada.

## Contribuições de Nenkai

- [Utility Mod Loader](https://github.com/Nenkai/fftivc.utility.modloader): sobrescrita de arquivos/tabelas em runtime e base de compatibilidade.
- [FF16Tools](https://github.com/Nenkai/FF16Tools): extração, ferramentas NEX/tabelas e conversão de texturas usadas no desenvolvimento.
- [FaithFramework](https://github.com/Nenkai/FaithFramework): referência do engine/runtime e ferramentas de investigação.

**Nenkai** é o autor original dessas ferramentas, não ZeroDS. Os avisos MIT foram preservados em `reference-licenses`.

## Runtime e compatibilidade

- [Reloaded-II / Reloaded Project](https://github.com/Reloaded-Project/Reloaded-II): runtime integrado e interfaces; código correspondente GPL-3.0 fornecido no repositório FFTModLoader.
- [FFTGenericJobs / cipherxof (MIT notice: trigger)](https://github.com/cipherxof/FFTGenericJobs): base de compatibilidade das classes genéricas; aviso MIT original preservado.
- Reloaded SharedLib.Hooks e Memory/SigScan: hooks e varredura, com metadados e avisos originais.
- dll_syringe, MinHook, nlohmann/json, Microsoft .NET e Visual C++: componentes de execução/compilação e licenças originais preservados.

## Ferramentas realmente usadas

- Visual Studio 2022 (MSVC e MASM x64), CMake: compilação do launcher e bridge nativo.
- SDK .NET, NuGet, PowerShell: módulos gerenciados, scripts de compilação/instalação e testes.
- Python 3 e Pillow: geração de conteúdo, processamento de sprites, hashes e empacotamento.
- FF16Tools: extração e conversão dos formatos do jogo.
- [FFT Ivalice Chronicles - Sprite Modding Toolkit / Kanaruu](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/20): comparação de filtros e resultados Scale2x dos sprites/retratos de Generic Knights. Código descompilado do Toolkit não é redistribuído.
- SixLabors ImageSharp e ferramentas DDS: filtros e visualizações de desenvolvimento, sob suas próprias licenças.
- [ILSpy / ICSharpCode](https://github.com/icsharpcode/ILSpy): inspeção de assemblies sem modificá-los.
- [Iced.Intel](https://github.com/icedland/iced): análise/testes de instruções nativas.
- Git e GitHub CLI: controle de versão e publicação.
- FaithFramework e sondas nativas de leitura do workspace: investigação do engine e recursos.

Ferramentas de desenvolvimento não são necessariamente dependências de execução: para jogar, bastam os pacotes de release e a instalação legítima do jogo.
FINAL FANTASY TACTICS e seus recursos originais pertencem aos respectivos proprietários.
Arte de terceiros não é relicenciada como código original de ZeroDS. Consulte a tabela e `SPRITE_CREDITS.json` de Generic Knights.
