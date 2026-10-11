# Building FFTModLoader

[English](BUILD.md) | [Português](BUILD.pt-BR.md)

Windows x64; Visual Studio 2022 with C++/MASM and CMake 3.24+; .NET SDK supporting net8/net9.
Run `./build-source.ps1` to build the launcher, RuntimeLogRelayTests, JobExpansion and ContentExpansion.
This does not install files into the game or change saves.

- `src/ContentExpansionApi`: shared controller contract.
- `src/ContentExpansionModule`: loader-owned provider, compiling Core/Runtime sources.
- `src/ContentExpansionCore`, `Runtime`, `Native`: tables, formulas, saves, native routes and bridge.
- `tools/build_native_bridge.ps1`: native bridge build.
- `vendor/Reloaded-II`: full locally modified corresponding runtime source, with original GPL license.

For integration tests, check out ReworkedChemist as well and pass
`-p:ReworkedChemistSourceDirectory="path/to/ReworkedChemist/AlchemistRework"`
to `dotnet run --project tests/ContentExpansionModuleTests -c Release`.
The tests check shared exports, dormant provider registration, one-time activation, missing dependency and incompatible API refusal.
Core/runtime tests are under `tests`; native ABI tests need a legal local game executable and installed hook libraries.
The host's comprehensive owned-memory tests are in ReworkedChemist's source repository and reference this module.

Do not treat synthetic tests as an in-game/GPU validation.
Rebuilding produces development binaries; it does not certify the new build as player-approved.
No native game executable, complete archive, private save or recording belongs in publication.
