# FFTModLoader

[English](README.md) | [Português](README.pt-BR.md)

Author: **ZeroDS** · [Source and releases](https://github.com/andersongz0/FFTModLoader)

FFTModLoader loads compatible mods into **FINAL FANTASY TACTICS - The Ivalice Chronicles (Enhanced)** on Windows. It starts the game with the bundled runtime, resolves mod dependencies and provides integrated expansions for additional jobs, items, abilities, menus, shops and AI support.

**GenericJobs is built into FFTModLoader** and makes **Dark Knight** and **Onion Knight** available as generic jobs. **Holy Knight** and **Rune Knight** are added separately by Generic Knights. Additional items and Chemist changes come from Reworked Chemist.

## Requirements

- Windows x64 and a legal Steam installation of **FINAL FANTASY TACTICS - The Ivalice Chronicles**, Enhanced mode.
- The complete FFTModLoader release ZIP.

## FFTModLoader Installation Guide

1. Close the game. Close the loader too if it is already installed and running.
2. Download **FFTModLoader-0.11.7-rc.9.zip** from [release Assets](https://github.com/andersongz0/FFTModLoader/releases/tag/v0.11.7-rc.9), not **Source code**.
3. Extract the ZIP to a separate folder outside the game installation.
4. Double-click **Install.cmd** and choose your language.
5. Check the detected folder and type **YES** to confirm. If several installations are found, choose one; if none is found, enter the path to **FFT_enhanced.exe** or its folder.
6. Approve the Windows permission prompt, if shown, and wait for completion.

No terminal commands are needed. The installer verifies the package and backs up replaced files under **FFTModLoader.Backup** in the game folder. Saves and unrelated mods are preserved.

## Using FFTModLoader

Start **FFTModLoader.exe** from the game folder. Keep the accompanying **FFTModLoader.Runtime** and compatibility folders in place.

## Integrated expansions

Both are part of the loader, not separate user-installed mods:

- `FFTModLoader.Runtime/InternalMods/JobExpansion`: job slots, job commands and visual resource routing.
- `FFTModLoader.Runtime/InternalMods/ContentExpansion`: item/ability/formula tables, inventory and save infrastructure, native bridge, menus, shops and AI routes.

JobExpansion activates the built-in GenericJobs classes and any jobs registered by compatible mods. ContentExpansion enables additional gameplay only when requested by a compatible mod.


See [verification details](VERIFICATION.md) for the supported executable.

## Updating

Close the game and any running loader, then run **Install.cmd** from the new complete release. If dependency files are missing, reinstall the complete FFTModLoader package.

## Credits

**Nenkai** created [Utility Mod Loader](https://github.com/Nenkai/fftivc.utility.modloader),
[FF16Tools](https://github.com/Nenkai/FF16Tools) and [FaithFramework](https://github.com/Nenkai/FaithFramework).
These contributions are foundational to this project. The original dependency authors and licenses remain intact.

Included: Utility Mod Loader, original GenericJobs (cipherxof; MIT notice: trigger), SigScan, SharedLib.Hooks, integrated Reloaded-II runtime and .NET.
The corresponding modified source is in `vendor/Reloaded-II`.
No game executable, complete PAC archive, user save or private test recording is included.

See [credits and tools](CREDITS.md), [build instructions](BUILD.md) and [third-party notices](THIRD_PARTY_NOTICES.md).
