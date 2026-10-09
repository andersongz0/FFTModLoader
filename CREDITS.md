# Credits and development tools

[English](CREDITS.md) | [Português](CREDITS.pt-BR.md)

Original mod/loader integration: **ZeroDS**. Third-party authorship is preserved.

## Nenkai's contributions

- [Utility Mod Loader](https://github.com/Nenkai/fftivc.utility.modloader): runtime file/table overrides and compatibility foundation.
- [FF16Tools](https://github.com/Nenkai/FF16Tools): extraction, NEX/table tooling and texture conversion used during development.
- [FaithFramework](https://github.com/Nenkai/FaithFramework): engine/runtime reference and investigation tools.

Nenkai is the original author of these tools, not ZeroDS. Their MIT notices are retained in `reference-licenses`.

## Runtime and compatibility

- [Reloaded-II / Reloaded Project](https://github.com/Reloaded-Project/Reloaded-II): integrated mod runtime and interfaces; GPL-3.0 corresponding source is supplied in the FFTModLoader repository.
- [FFTGenericJobs / cipherxof (MIT notice: trigger)](https://github.com/cipherxof/FFTGenericJobs): original generic-job compatibility basis; original MIT notice retained.
- Reloaded SharedLib.Hooks and Memory/SigScan: hooks and scanning dependencies, with original package metadata and notices.
- dll_syringe, MinHook, nlohmann/json, Microsoft .NET and Visual C++ runtimes: original third-party runtime/build components and licenses preserved.

## Tools actually used

- Visual Studio 2022 (MSVC and MASM x64), CMake: launcher and native bridge builds.
- .NET SDK, NuGet, PowerShell: managed modules, build/installation scripts and regression tests.
- Python 3 and Pillow: content generators, sprite processing, checksums and packaging.
- FF16Tools: game-format extraction and conversion.
- [FFT Ivalice Chronicles - Sprite Modding Toolkit / Kanaruu](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/20): Generic Knights filter comparisons and selected Scale2x sprite/portrait outputs. Decompiled Toolkit code is not redistributed.
- SixLabors ImageSharp and DDS tooling: development image/filter previews, under their own licenses.
- [ILSpy / ICSharpCode](https://github.com/icsharpcode/ILSpy): read-only assembly inspection.
- [Iced.Intel](https://github.com/icedland/iced): native instruction analysis/tests.
- Git and GitHub CLI: version control and publication.
- FaithFramework and the workspace's native read-only probes: engine/resource investigation.

The list distinguishes development tools from required runtime dependencies; users need only the release packages and their legal game installation.
FINAL FANTASY TACTICS and original game assets belong to their respective owners.
Third-party artwork is not relicensed as original ZeroDS code. See Generic Knights' sprite credit table and `SPRITE_CREDITS.json`.
