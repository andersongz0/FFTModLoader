# Candidate verification — 2026-10-09

[English](#english) | [Português](#português)

## English

Candidate set: FFTModLoader 0.11.7-rc.1, Generic Knights 0.1.26-rc.1,
Reworked Chemist 0.2.44-rc.1. The previous stable releases remain available.

- Public managed sources compile without warnings/errors; eight provider/consumer lifecycle tests pass.
- Actual compiled ContentExpansion host regression tests pass: medicine menus and stock rejection,
  menu closure/back navigation, drawing order, chapter shops, flask formula, native interception,
  save/GC lifetime and AI routes. Native owned-memory/ABI regression tests also pass.
- All three actual release installers pass in an isolated clean folder. Preparation resolves eight unique
  mod IDs with complete dependency/DLL closure and both loader-owned expansions.
- Legacy Chemist folder migrates to a recoverable backup. Test save sentinel and game executable remain unchanged.
- Reworked Chemist gameplay resources and the approved native bridge are byte-for-byte preserved.
  Provider/consumer managed binaries were rebuilt for the architecture migration.
- Generic Knights gameplay artwork/data are unchanged except project metadata and credit/documentation additions.
- Publication audit excludes game executables/archives, saves, private recordings, logs, dumps and credentials.

Preparation and owned-memory tests do **not** run a game scene. A live clean-folder gameplay test
by the user is still pending; these releases are prerelease test candidates, not a new gameplay approval.
Only publish new mods after the user's explicit finalized decision.

## Português

Conjunto candidato: FFTModLoader 0.11.7-rc.1, Generic Knights 0.1.26-rc.1,
Reworked Chemist 0.2.44-rc.1. As releases estáveis anteriores continuam disponíveis.

- Fontes gerenciadas públicas compilam sem avisos/erros; oito testes do ciclo provider/consumer passaram.
- Testes do host ContentExpansion realmente compilado passaram: menus de itens e estoque zerado,
  fechamento/volta dos menus, ordem de desenho, lojas por capítulo, fórmula dos frascos, interceptação nativa,
  saves/GC e rotas da IA. Os testes nativos em memória própria/ABI também passaram.
- Os três instaladores reais passaram em uma pasta limpa isolada. A preparação resolve oito IDs únicos,
  todas as dependências/DLLs e as duas expansões internas do loader.
- A pasta antiga do Chemist é migrada para backup recuperável. O arquivo de teste de save e o executável não mudaram.
- Recursos de gameplay do Chemist e bridge nativo aprovado permanecem idênticos byte a byte.
  Os binários gerenciados provider/consumer foram recompilados para a migração da arquitetura.
- Arte/dados do Generic Knights não mudaram, exceto metadados e documentação/créditos adicionais.
- Auditoria exclui executáveis/arquivos completos do jogo, saves, vídeos privados, logs, dumps e credenciais.

A preparação e os testes em memória própria **não** executam uma cena do jogo. Falta o teste
de gameplay em pasta limpa pelo usuário; estas releases são candidatas de teste, não uma nova aprovação.
Novos mods só serão publicados após decisão explícita do usuário de que estão finalizados.
