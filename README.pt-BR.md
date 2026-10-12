# FFTModLoader

[English](README.md) | [Português](README.pt-BR.md)

Autor: **ZeroDS**

**0.11.8-rc.1** é uma pré-release de teste. O runtime integrado não procura a Steam, não intercepta funções da SteamAPI e não grava `steam_appid.txt`. Os requisitos próprios do jogo permanecem inalterados.

Carrega mods compatíveis em **FINAL FANTASY TACTICS - The Ivalice Chronicles (Enhanced)**. Inclui o runtime, o gerenciamento de dependências e as expansões para novas classes, itens, habilidades e sistemas de batalha. O console mostra o conteúdo carregado por cada mod.

**Dark Knight e Onion Knight** estão incluídas como classes genéricas. Compatível com **Generic Knights** e **Reworked Chemist**; esses mods são baixados separadamente em seus próprios repositórios.

## Requisitos

- Windows x64.
- Uma instalação legítima do jogo com o **FFT_enhanced.exe** suportado. O modo Classic não é compatível.
- O pacote completo da release do FFTModLoader.

## Guia de Instalação do FFTModLoader

1. Feche o jogo e, se estiver em execução, o loader.
2. Baixe **FFTModLoader-0.11.8-rc.1.zip** em [Releases](https://github.com/andersongz0/FFTModLoader/releases/tag/v0.11.8-rc.1), não **Source code**.
3. Extraia o ZIP fora da pasta do jogo.
4. Dê dois cliques em **Install.cmd** e escolha seu idioma.
5. Confirme a pasta do jogo encontrada. Se ela não for encontrada, selecione a pasta que contém **FFT_enhanced.exe**.
6. Autorize a permissão do Windows, se solicitada, e aguarde a conclusão.

O instalador verifica a integridade do pacote e faz backup dos arquivos substituídos. Saves e outros mods são preservados.

## Uso e atualização

Abra **FFTModLoader.exe** na pasta do jogo. Mantenha **FFTModLoader.Runtime** e os arquivos de compatibilidade que acompanham o pacote. Para atualizar, feche o jogo e o loader e execute **Install.cmd** do novo pacote.

## Créditos

**Nenkai**: [Utility Mod Loader](https://github.com/Nenkai/fftivc.utility.modloader), [FF16Tools](https://github.com/Nenkai/FF16Tools) e [FaithFramework](https://github.com/Nenkai/FaithFramework). GenericJobs original, Reloaded-II e as demais dependências incluídas mantêm seus autores e suas licenças.

[Créditos e ferramentas](CREDITS.pt-BR.md) · [Avisos de terceiros](THIRD_PARTY_NOTICES.md) · [Compilação](BUILD.pt-BR.md) · [Executável suportado](VERIFICATION.md)
