# FFTModLoader

[English](README.md) | [Português](README.pt-BR.md)

Author: **ZeroDS** · [Source and releases](https://github.com/andersongz0/FFTModLoader)

Windows x64 loader for **FINAL FANTASY TACTICS - The Ivalice Chronicles (Enhanced)**.
**0.11.7-rc.1** is a clean-install test candidate, not a new gameplay approval.
The previous stable release remains available.

## Clean installation

1. Close the game and the loader.
2. Download the release **FFTModLoader-0.11.7-rc.1.zip**, not GitHub's source archive.
3. Extract outside the game folder. Run `install.ps1 -GameDirectory "your game folder"`.
4. Install [Generic Knights](https://github.com/andersongz0/GenericKnights/releases) and/or [Reworked Chemist](https://github.com/andersongz0/ReworkedChemist/releases) the same way.
5. Start **FFTModLoader.exe** from the game folder.

The installer verifies checksums, backs up overwritten files and leaves saves and unrelated mods alone.
Requires a legal Steam installation. Classic mode is not supported by these expansions.
The supported Enhanced executable SHA-256 is
`937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5`.

## Integrated expansions

Both are part of the loader, not separate user-installed mods:

- `FFTModLoader.Runtime/InternalMods/JobExpansion`: job slots, job commands and visual resource routing.
- `FFTModLoader.Runtime/InternalMods/ContentExpansion`: item/ability/formula tables, inventory and save infrastructure, native bridge, menus, shops and AI routes.

ContentExpansion registers a shared API but does not apply Chemist gameplay unless Reworked Chemist requests it.
This first API supports the current Chemist integration; it is not a promise of arbitrary new mod support without development.
Generic Knights and Reworked Chemist remain separate content packages and do not depend on each other.

## Compatibility components and credits

**Nenkai** created [Utility Mod Loader](https://github.com/Nenkai/fftivc.utility.modloader),
[FF16Tools](https://github.com/Nenkai/FF16Tools) and [FaithFramework](https://github.com/Nenkai/FaithFramework).
These contributions are foundational to this project. The original dependency authors and licenses remain intact.

Included: Utility Mod Loader, original GenericJobs (cipherxof; MIT notice: trigger), SigScan, SharedLib.Hooks, integrated Reloaded-II runtime and .NET.
No separate Reloaded-II desktop installation is required. Its components are still used internally and credited;
the corresponding modified source is in `vendor/Reloaded-II`.
No game executable, complete PAC archive, user save or private test recording is included.

See [credits and tools](CREDITS.md), [build instructions](BUILD.md) and [third-party notices](THIRD_PARTY_NOTICES.md).
New mods will only be published after the user explicitly decides they are finalized.
