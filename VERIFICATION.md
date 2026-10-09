# Verification / Verificação

## English

rc.6 corrects the JobExpansion callback at Enhanced RVA 28A6E8: it returns the native integer ability count instead of discarding it through a void delegate. The non-equipped-job Learn route (2BA1FC) uses that count; the equipped-job route uses a different helper (28A770). The actual callback is exercised across 9,721 native-ABI fixture calls covering job IDs, action/passive categories, modes, empty/nonempty lists, native rows/terminators and the existing Dark Knight remap. No equipped job, learning bits or save is changed by this hook. The user's video demonstrates the old failure; this build still requires their in-game confirmation. Existing Generic Knights and Reworked Chemist rc.3 packages do not need to be reinstalled.

rc.5 adds a canonical Reworked Chemist ID (`ffttic.jobs.reworkedchemist`), translates old consumer/dependency IDs in the generated view, and uses deterministic dependency-first ordering. The console shows component names in the same order written to the runtime configuration. Older Generic Knights packages receive their missing JobExpansion dependency in this generated view only. Original third-party files and expanded saves are not rewritten. Checks cover old-to-new installations, duplicate aliases, missing dependencies, cycles and order stability.

The rc.4 loader restores GenericJobs as built-in loader content. Dark Knight (160 / A0) and Onion Knight (161 / A1) are always registered by the bundled GenericJobs. Only Holy Knight (162 / A2) and Rune Knight (163 / A3) depend on Generic Knights. This corrects rc.3, which incorrectly disabled the built-in classes together with the absent Knights classes. Existing Generic Knights and Reworked Chemist rc.2 packages remain compatible without reinstalling them.

Job registrations use `FFTModLoader.jobs.json` with `SchemaVersion: 1` and a numeric `JobIds` array (160–255). The resolved view registers A0/A1 for GenericJobs and supplies backward-compatible A2/A3 registration for the existing Generic Knights package. GenericJobs gameplay resources are always available; only its original DLL is suppressed because JobExpansion owns the hooks. Original third-party files remain unchanged.

Checks include built-in versus optional registrations, stale four-slot settings, duplicate and invalid IDs, upgrades from rc.2 and rc.3, and runtime preparation for loader-only, Knights-only, Chemist-only, both mods, and Knights removal/reinstallation. Dark/Onion must remain registered in every combination; Holy/Rune must only appear with Knights. All original GenericJobs assets must be present and unchanged in the resolved view. These are automated preparation and policy checks, not live-game verification. Artwork, Chemist content, ContentExpansion and the approved native bridge are unchanged.

Installer checks cover Steam-library discovery, multiple installations, manual selection, cancellation, literal path escaping, package integrity, missing dependencies and clean three-package installation in an isolated folder. Runtime preparation resolves eight unique mod IDs and both integrated expansions. Backup, legacy folder migration and save/executable preservation are checked.

These checks do not run a live game scene. The clean-install gameplay test remains pending. Releases are labeled prerelease until that test is approved.

Supported Enhanced executable SHA-256:
`937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5`.

## Português

O rc.6 corrige o callback do JobExpansion no RVA Enhanced 28A6E8: ele passa a retornar a quantidade nativa de habilidades, antes descartada por um delegate sem retorno. O Learn de uma classe não equipada (2BA1FC) usa essa quantidade; a classe equipada usa outro helper (28A770). O callback real passou por 9.721 chamadas de teste na ABI nativa, cobrindo classes, categorias de ação/passivas, modos, listas vazias/preenchidas, linhas/terminadores nativos e a compatibilidade do Dark Knight. Esse hook não altera a classe equipada, habilidades aprendidas ou saves. O vídeo do usuário demonstra o erro anterior; esta compilação ainda requer sua confirmação dentro do jogo. Os pacotes Generic Knights e Reworked Chemist rc.3 continuam compatíveis sem reinstalação.

O rc.5 usa o ID definitivo do Reworked Chemist (`ffttic.jobs.reworkedchemist`), traduz os IDs antigos de mods/dependências na cópia de execução e aplica uma ordem determinística com dependências primeiro. O console mostra os nomes dos componentes na mesma ordem gravada na configuração do runtime. Pacotes antigos do Generic Knights recebem o vínculo com JobExpansion somente nessa cópia gerada. Arquivos originais de terceiros e saves expandidos não são reescritos. As verificações cobrem atualização antiga/nova, aliases duplicados, dependências ausentes, ciclos e estabilidade da ordem.

O loader rc.4 restaura o GenericJobs como conteúdo integrado ao loader. Dark Knight (160 / A0) e Onion Knight (161 / A1) são sempre registradas pelo GenericJobs incluído. Apenas Holy Knight (162 / A2) e Rune Knight (163 / A3) dependem do Generic Knights. Isso corrige o rc.3, que desativou indevidamente as classes integradas junto das classes ausentes do Knights. Os pacotes existentes Generic Knights e Reworked Chemist rc.2 permanecem compatíveis, sem reinstalá-los.

O registro usa `FFTModLoader.jobs.json` com `SchemaVersion: 1` e um array numérico `JobIds` (160–255). A cópia de execução registra A0/A1 para GenericJobs e A2/A3 para o pacote existente do Generic Knights. Os recursos de gameplay do GenericJobs ficam sempre disponíveis; somente sua DLL original é desativada, pois o JobExpansion fornece os hooks. Os arquivos originais de terceiros permanecem intactos.

As verificações incluem registros integrados/opcionais, configurações antigas de quatro classes, IDs duplicados/inválidos, atualização de rc.2 e rc.3 e preparação do runtime com loader sozinho, somente Knights, somente Chemist, ambos os mods e remoção/reinstalação do Knights. Dark/Onion devem permanecer registradas em todas as combinações; Holy/Rune apenas com Knights. Todos os recursos originais do GenericJobs devem estar presentes e intactos na cópia de execução. São verificações automatizadas de preparação e ativação, não testes dentro do jogo. Arte, conteúdo do Chemist, ContentExpansion e o bridge nativo aprovado permanecem intactos.

As verificações do instalador cobrem busca nas bibliotecas Steam, várias instalações, seleção manual, cancelamento, caminhos com caracteres especiais, integridade do pacote, dependências ausentes e instalação dos três pacotes em uma pasta isolada. A preparação resolve oito IDs de mods e as duas expansões integradas. Backup, migração da pasta antiga e preservação dos saves/executável são verificados.

Essas verificações não executam uma cena do jogo. O teste de gameplay em instalação limpa continua pendente. As releases são marcadas como preliminares até a aprovação desse teste.

SHA-256 do executável Enhanced suportado:
`937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5`.
