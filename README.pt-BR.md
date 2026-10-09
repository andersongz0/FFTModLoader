# FFTModLoader

[English](README.md) | [Português](README.pt-BR.md)

Autor: **ZeroDS** · [Código-fonte e releases](https://github.com/andersongz0/FFTModLoader)

Loader Windows x64 para **FINAL FANTASY TACTICS - The Ivalice Chronicles (Enhanced)**.
**0.11.7-rc.1** é uma versão preliminar disponível para teste.
A versão estável anterior continua disponível.

## Requisitos

- Windows x64.
- Instalação legítima da Steam de **FINAL FANTASY TACTICS - The Ivalice Chronicles**, modo Enhanced.
- Pacote completo do FFTModLoader. Runtime e componentes de compatibilidade estão incluídos; não é necessário instalar a interface do Reloaded-II separadamente.

## Guia de Instalação do FFTModLoader

1. Feche o jogo e o loader.
2. Baixe **FFTModLoader-0.11.7-rc.1.zip** em [Assets da release](https://github.com/andersongz0/FFTModLoader/releases/tag/v0.11.7-rc.1), não os arquivos **Source code**.
3. Extraia o ZIP em uma pasta fora da instalação do jogo.
4. Abra o PowerShell na pasta extraída e execute `./install.ps1 -GameDirectory "pasta do jogo"`. Informe a pasta que contém **FFT_enhanced.exe**.
5. Abra **FFTModLoader.exe** na pasta do jogo. Mantenha `FFTModLoader.Runtime` e as pastas de compatibilidade instaladas junto dele.

O instalador confere os hashes e faz backup dos arquivos substituídos. Não modifica saves nem outros mods.
Requer uma instalação legítima da Steam. As expansões não são preparadas para o modo Classic.
SHA-256 do executável Enhanced suportado:
`937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5`.

## Expansões integradas

As duas fazem parte do loader, sem instalação separada:

- `FFTModLoader.Runtime/InternalMods/JobExpansion`: classes, comandos e roteamento dos recursos visuais extras.
- `FFTModLoader.Runtime/InternalMods/ContentExpansion`: tabelas de itens/habilidades/fórmulas, inventário, saves, bridge nativo, menus, lojas e rotas da IA.

As funções das expansões são ativadas por um mod consumidor compatível; instalar somente o loader não ativa o conteúdo desse mod.
Isso não significa que qualquer mod novo seja compatível sem desenvolvimento adicional.

## Atualização e solução de problemas

Feche jogo e loader antes de executar o instalador da nova release. Arquivos substituídos ficam em backup em **FFTModLoader.Backup**.
Se o loader informar arquivos internos ausentes, reinstale o pacote completo em vez de copiar apenas o executável.
Uma versão do executável do jogo não suportada exige atualização compatível do loader; não ignore essa verificação.

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
