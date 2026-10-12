# Steam-neutral runtime regression / Teste de runtime neutro

Build the runtime from `vendor/Reloaded-II/source/Reloaded.Mod.Loader` in Release,
AnyCPU/x64, and put its DLL and dependency manifest into an isolated complete
loader package. Keep the package's shared hook library and existing gameplay
components unchanged.

Build `SteamApiTestFixture` with CMake. Run this project in separate processes,
passing the isolated package's `FFTModLoader.Runtime/Loader/X64` folder, then
`absent` or `present`. For `present`, pass the absolute path to the test fixture
DLL as the third argument. The fixture is never a game/distribution dependency.

The test invokes the compiled runtime's private platform setup using the real
shared hook provider, then disables diagnostic hooks before fixture exit. It
requires only exit/crash hooks, zero SteamAPI calls/changes, no Steam discovery
references and an unchanged app-id sentinel. The previous runtime fails the
compiled-route check before installing any hooks.

Compile o runtime e o fixture em Release e use um pacote completo isolado. Rode
os casos `absent` e `present` em processos separados. No caso `present`, passe
também o caminho absoluto da DLL de teste. O teste confirma que somente os
hooks de encerramento/crash são instalados e que a SteamAPI e o app-id ficam
intactos. A DLL de teste nunca deve acompanhar o pacote do loader.
