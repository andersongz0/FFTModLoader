# Código-fonte

Autor do projeto: ZeroDS. Windows x64, Visual Studio 2022 com C++ e CMake 3.24+.
Para os projetos gerenciados, instale o SDK .NET 9 (ou um SDK compatível com net8/net9).

Execute `./build-source.ps1`. Ele compila o launcher, o teste do console e o
JobExpansion, sem instalar nada no jogo nem modificar saves. Os pacotes NuGet
e as dependências CMake são restaurados de seus projetos originais.

`src/Native` contém também a arquitetura histórica do protótipo. O launcher
0.11.6 usa o runtime integrado; não confunda o DLL histórico com o bootstrapper.

O código completo correspondente ao runtime Reloaded-II redistribuído está em
`vendor/Reloaded-II`, incluindo as extensões locais em LoaderConfig/dllmain e
na leitura de configuração. Consulte as instruções e a licença desse projeto
para compilá-lo. A publicação preserva os binários aprovados em vez de recompilar
dependências sem necessidade.

`src/ContentExpansionCore`, `src/ContentExpansionRuntime` e
`src/ContentExpansionNative` são os componentes compartilhados do Reworked
Chemist. O projeto que compila seu DLL final e os testes específicos estão no
[repositório ReworkedChemist](https://github.com/andersongz0/ReworkedChemist).

As fontes não contêm dados completos extraídos do jogo. Testes que usam tabelas,
recursos ou executáveis nativos exigem uma instalação legal fornecida localmente.
