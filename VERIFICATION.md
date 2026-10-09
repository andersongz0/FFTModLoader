# Verification / Verificação

## English

The rc.3 loader fixes class activation when no gameplay mod is installed. Bundled GenericJobs gameplay resources are exposed only when requested by a job consumer. JobExpansion reads registered jobs rather than creating four default slots. With no consumer it does not install job-menu or sparse-table hooks; with Reworked Chemist alone it enables sparse lookup without changing the class menu. Existing Generic Knights rc.2 packages are supported without reinstalling the mod.

Job registrations use `FFTModLoader.jobs.json` with `SchemaVersion: 1`, a numeric `JobIds` array (160–255), and `RequiresGenericJobsContent: true` only when the consumer needs the bundled GenericJobs content. The resolved view supplies backward-compatible registration for the existing Generic Knights package. Original third-party files remain unchanged.

Checks include registered/absent jobs, stale settings, duplicate and invalid IDs, and runtime preparation for loader-only, Knights-only, Chemist-only, both mods, and consumer removal/reinstallation. These are automated preparation and policy checks, not live-game verification. Artwork, Chemist content, ContentExpansion and the approved native bridge are unchanged.

Installer checks cover Steam-library discovery, multiple installations, manual selection, cancellation, literal path escaping, package integrity, missing dependencies and clean three-package installation in an isolated folder. Runtime preparation resolves eight unique mod IDs and both integrated expansions. Backup, legacy folder migration and save/executable preservation are checked.

These checks do not run a live game scene. The clean-install gameplay test remains pending. Releases are labeled prerelease until that test is approved.

Supported Enhanced executable SHA-256:
`937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5`.

## Português

O loader rc.3 corrige a ativação de classes sem mods de gameplay instalados. Os recursos de gameplay do GenericJobs incluído só são disponibilizados quando solicitados por um mod de classes. O JobExpansion usa classes registradas, em vez de criar quatro entradas padrão. Sem consumidores, não instala alterações do menu de classes ou da busca de tabelas; com apenas Reworked Chemist, ativa a busca de tabelas sem modificar o menu de classes. O pacote existente do Generic Knights rc.2 continua compatível, sem reinstalar o mod.

O registro usa `FFTModLoader.jobs.json` com `SchemaVersion: 1`, um array numérico `JobIds` (160–255) e `RequiresGenericJobsContent: true` somente quando o mod precisa do conteúdo GenericJobs incluído. A cópia de execução registra automaticamente o pacote existente do Generic Knights. Os arquivos originais de terceiros permanecem intactos.

As verificações incluem classes registradas/ausentes, configurações antigas, IDs duplicados/inválidos e preparação do runtime com loader sozinho, somente Knights, somente Chemist, ambos os mods e remoção/reinstalação do consumidor. São verificações automatizadas de preparação e ativação, não testes dentro do jogo. Arte, conteúdo do Chemist, ContentExpansion e o bridge nativo aprovado permanecem intactos.

As verificações do instalador cobrem busca nas bibliotecas Steam, várias instalações, seleção manual, cancelamento, caminhos com caracteres especiais, integridade do pacote, dependências ausentes e instalação dos três pacotes em uma pasta isolada. A preparação resolve oito IDs de mods e as duas expansões integradas. Backup, migração da pasta antiga e preservação dos saves/executável são verificados.

Essas verificações não executam uma cena do jogo. O teste de gameplay em instalação limpa continua pendente. As releases são marcadas como preliminares até a aprovação desse teste.

SHA-256 do executável Enhanced suportado:
`937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5`.
