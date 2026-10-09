# FFTModLoader

[English](README.md) | [Português](README.pt-BR.md)

Author: **ZeroDS** · [Source and releases](https://github.com/andersongz0/FFTModLoader)

Windows x64 loader for **FINAL FANTASY TACTICS - The Ivalice Chronicles (Enhanced)**.
**0.11.7-rc.1** is a prerelease available for testing.
The previous stable release remains available.

## Requirements

- Windows x64.
- A legal Steam installation of **FINAL FANTASY TACTICS - The Ivalice Chronicles**, Enhanced mode.
- The complete FFTModLoader release package. Its runtime and compatibility components are included; no separate Reloaded-II desktop installation is required.

## FFTModLoader Installation Guide

1. Close the game and the loader.
2. Download **FFTModLoader-0.11.7-rc.1.zip** from [release Assets](https://github.com/andersongz0/FFTModLoader/releases/tag/v0.11.7-rc.1), not GitHub's **Source code** archive.
3. Extract the ZIP into a folder outside the game installation.
4. Open PowerShell in the extracted folder and run `./install.ps1 -GameDirectory "your game folder"`. Select the folder containing **FFT_enhanced.exe**.
5. Start **FFTModLoader.exe** from the game folder. Keep `FFTModLoader.Runtime` and the bundled compatibility folders installed alongside it.

The installer verifies checksums, backs up overwritten files and leaves saves and unrelated mods alone.
Requires a legal Steam installation. Classic mode is not supported by these expansions.
The supported Enhanced executable SHA-256 is
`937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5`.

## Integrated expansions

Both are part of the loader, not separate user-installed mods:

- `FFTModLoader.Runtime/InternalMods/JobExpansion`: job slots, job commands and visual resource routing.
- `FFTModLoader.Runtime/InternalMods/ContentExpansion`: item/ability/formula tables, inventory and save infrastructure, native bridge, menus, shops and AI routes.

Expansion functionality is activated by a compatible consuming mod; installing the loader alone does not enable that mod's content.
This does not imply that arbitrary new mods are supported without further development.

## Updating and troubleshooting

Close the game and loader before running the new release's installer. Replaced files are backed up under **FFTModLoader.Backup**.
If the loader reports missing internal runtime files, reinstall the complete package rather than copying only the executable.
An unsupported game executable requires a compatible loader update; do not bypass this validation.

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
