# FFTModLoader

[English](README.md) | [Português](README.pt-BR.md)

Autor: **ZeroDS** · [Código-fonte e releases](https://github.com/andersongz0/FFTModLoader)

Loader Windows x64 para **FINAL FANTASY TACTICS - The Ivalice Chronicles (Enhanced)**.
**0.11.7-rc.1** é uma candidata para testar a instalação limpa, ainda não uma nova aprovação de gameplay.
A versão estável anterior continua disponível.

## Instalação limpa

1. Feche o jogo e o loader.
2. Baixe **FFTModLoader-0.11.7-rc.1.zip** na release, não o arquivo de código-fonte automático do GitHub.
3. Extraia fora da pasta do jogo. Execute `install.ps1 -GameDirectory "pasta do jogo"`.
4. Instale [Generic Knights](https://github.com/andersongz0/GenericKnights/releases) e/ou [Reworked Chemist](https://github.com/andersongz0/ReworkedChemist/releases) da mesma maneira.
5. Abra **FFTModLoader.exe** na pasta do jogo.

O instalador confere os hashes e faz backup dos arquivos substituídos. Não modifica saves nem outros mods.
Requer uma instalação legítima da Steam. As expansões não são preparadas para o modo Classic.
SHA-256 do executável Enhanced suportado:
`937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5`.

## Expansões integradas

As duas fazem parte do loader, sem instalação separada:

- `FFTModLoader.Runtime/InternalMods/JobExpansion`: classes, comandos e roteamento dos recursos visuais extras.
- `FFTModLoader.Runtime/InternalMods/ContentExpansion`: tabelas de itens/habilidades/fórmulas, inventário, saves, bridge nativo, menus, lojas e rotas da IA.

ContentExpansion registra uma API compartilhada, mas só ativa o gameplay do Chemist quando Reworked Chemist solicita.
Esta primeira API atende à integração atual do Chemist; novos tipos de mod podem exigir desenvolvimento.
Generic Knights e Reworked Chemist são pacotes separados e não dependem um do outro.

## Compatibilidade e créditos

**Nenkai** criou [Utility Mod Loader](https://github.com/Nenkai/fftivc.utility.modloader),
[FF16Tools](https://github.com/Nenkai/FF16Tools) e [FaithFramework](https://github.com/Nenkai/FaithFramework).
Essas contribuições são fundamentais para o projeto. Autores e licenças das dependências foram preservados.

Incluídos: Utility Mod Loader, GenericJobs original (cipherxof; aviso MIT: trigger), SigScan, SharedLib.Hooks, runtime integrado Reloaded-II e .NET.
Não é necessário instalar a interface do Reloaded-II separadamente. Seus componentes continuam sendo usados e creditados.
O código correspondente, com as modificações locais, está em `vendor/Reloaded-II`.
Não distribuímos executável do jogo, PAC completo, saves nem vídeos privados de teste.

Veja [créditos e ferramentas](CREDITS.pt-BR.md), [compilação](BUILD.pt-BR.md) e [avisos de terceiros](THIRD_PARTY_NOTICES.md).
Novos mods só serão publicados quando o usuário decidir explicitamente que estão finalizados.
