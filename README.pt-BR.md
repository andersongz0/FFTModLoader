# FFTModLoader

[English](README.md) | [Português](README.pt-BR.md)

Autor: **ZeroDS** · [Código-fonte e releases](https://github.com/andersongz0/FFTModLoader)

FFTModLoader carrega mods compatíveis em **FINAL FANTASY TACTICS - The Ivalice Chronicles (Enhanced)** no Windows. Ele inicia o jogo com o runtime incluído, organiza as dependências dos mods e oferece expansões integradas para novas classes, itens, habilidades, menus, lojas e suporte à IA.

Instalar somente o loader não altera as classes ou os itens do jogo: esse conteúdo vem dos mods que você escolher instalar.

## Requisitos

- Windows x64 e instalação legítima de **FINAL FANTASY TACTICS - The Ivalice Chronicles** pela Steam, modo Enhanced.
- ZIP completo da release do FFTModLoader.

## Guia de Instalação do FFTModLoader

1. Feche o jogo. Feche também o loader, caso já esteja instalado e aberto.
2. Baixe **FFTModLoader-0.11.7-rc.3.zip** em [Assets da release](https://github.com/andersongz0/FFTModLoader/releases/tag/v0.11.7-rc.3), não **Source code**.
3. Extraia o ZIP em uma pasta separada, fora da instalação do jogo.
4. Dê dois cliques em **Install.cmd** e escolha o idioma.
5. Confira a pasta encontrada e digite **SIM** para confirmar. Se houver várias instalações, escolha uma; se nenhuma for encontrada, informe o caminho de **FFT_enhanced.exe** ou da pasta que o contém.
6. Autorize a solicitação de permissão do Windows, se aparecer, e aguarde a mensagem de conclusão.

Não é necessário digitar comandos. O instalador verifica o pacote e guarda arquivos substituídos em **FFTModLoader.Backup**, dentro da pasta do jogo. Saves e outros mods são preservados.

## Como usar

Abra **FFTModLoader.exe** na pasta do jogo. Mantenha a pasta **FFTModLoader.Runtime** e os componentes de compatibilidade que o acompanham.

## Expansões integradas

As duas fazem parte do loader, sem instalação separada:

- `FFTModLoader.Runtime/InternalMods/JobExpansion`: classes, comandos e roteamento dos recursos visuais extras.
- `FFTModLoader.Runtime/InternalMods/ContentExpansion`: tabelas de itens/habilidades/fórmulas, inventário, saves, bridge nativo, menus, lojas e rotas da IA.

As funções das expansões são ativadas por um mod consumidor compatível; instalar somente o loader não ativa o conteúdo desse mod.


Consulte [os detalhes de verificação](VERIFICATION.md) para o executável suportado.

## Atualização

Feche o jogo e o loader, se estiver aberto, e execute **Install.cmd** da nova release completa. Se faltarem arquivos de dependências, reinstale o pacote completo do FFTModLoader.

## Créditos

**Nenkai** criou [Utility Mod Loader](https://github.com/Nenkai/fftivc.utility.modloader),
[FF16Tools](https://github.com/Nenkai/FF16Tools) e [FaithFramework](https://github.com/Nenkai/FaithFramework).
Essas contribuições são fundamentais para o projeto. Autores e licenças das dependências foram preservados.

Incluídos: Utility Mod Loader, GenericJobs original (cipherxof; aviso MIT: trigger), SigScan, SharedLib.Hooks, runtime integrado Reloaded-II e .NET.
O código correspondente, com as modificações locais, está em `vendor/Reloaded-II`.
Não distribuímos executável do jogo, PAC completo, saves nem vídeos privados de teste.

Veja [créditos e ferramentas](CREDITS.pt-BR.md), [compilação](BUILD.pt-BR.md) e [avisos de terceiros](THIRD_PARTY_NOTICES.md).
