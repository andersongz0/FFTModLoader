# FFTModLoader

Autor: **ZeroDS** · [Código-fonte e releases](https://github.com/andersongz0/FFTModLoader)

Loader para **FINAL FANTASY TACTICS - The Ivalice Chronicles**, Windows x64.
Versão 0.11.6, com JobExpansion 0.2.7 integrado. Esta publicação mantém o runtime
da última instalação testada; o launcher recebeu apenas autoria e link do projeto.

## Instalação

Baixe `FFTModLoader-0.11.6.zip` na seção Releases e extraia em uma pasta separada.
Feche o jogo e execute `install.ps1 -GameDirectory "caminho da pasta do jogo"`.
O instalador preserva os saves e faz backup dos arquivos substituídos. Depois,
abra `FFTModLoader.exe` na pasta do jogo. Não execute o jogo em modo clássico
para testar os mods de expansão: eles foram preparados para `fft_enhanced.exe`.

O pacote inclui o runtime integrado e as dependências de compatibilidade:

- Utility Mod Loader (`fftivc.utility.modloader`, Nenkai).
- GenericJobs original (`ffttic.jobs.genericjobs`, trigger), requerido pela configuração do JobExpansion.
- SigScan e SharedLib.Hooks, com seus autores e identificadores originais.
- Runtime Reloaded-II, .NET e bibliotecas de execução necessárias.

Não exige instalar a interface do Reloaded-II separadamente. **Usa componentes
do Reloaded-II internamente**; seus créditos, licenças e código correspondente
estão preservados. Não inclui executáveis, arquivos PAC ou saves do jogo.

## Mods ZeroDS

- [Generic Knights](https://github.com/andersongz0/GenericKnights)
- [Reworked Chemist](https://github.com/andersongz0/ReworkedChemist)

Os dois mods são distribuídos separadamente. Não substitua dependências por
pastas com identificadores diferentes; o loader resolve os IDs originais.

Veja [BUILD.md](BUILD.md) e [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
Esta publicação não contém o antigo componente opcional de diagnóstico externo.
