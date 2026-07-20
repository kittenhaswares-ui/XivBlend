# XivBlend

XivBlend is an experimental Dalamud plugin that exports **your own currently displayed FFXIV character** into one packed Blender file.

The one-button prototype reads the final live draw object after Glamourer and Penumbra have applied the character's appearance. It exports the body, face, hair, visible equipment and weapons, materials, textures, morphs, skin weights, and deformation rig. Blender is then launched headlessly to build and save the `.blend`.

> **Prototype status:** version 0.0.2 has passed offline build and Blender validation, but still needs broader live in-game testing. Animations are deliberately deferred.

## What it exports

- Only Dalamud's logged-in `LocalPlayer`; there is no target or nearby-player selector.
- Body, face, hair, visible clothing, accessories, and visible weapons.
- Final Penumbra-resolved model, material, and texture data from the live draw object.
- Glamourer state and Penumbra resource-path diagnostics when their IPC APIs are available.
- Skeleton, skin weights, morphs, material colorsets, custom character colors, and textures.
- A Blender 5.0+ scene with a rig, organized collections, basic camera/light, mapped FFXIV materials, packed images, and an embedded snapshot manifest.

XivBlend rejects an export as incomplete when a visible model or material fails, or when Blender cannot verify a mesh bound to the imported armature.

## Requirements

- Windows x64 with FFXIV launched through XIVLauncher/Dalamud API 15.
- [Blender 5.0 or newer](https://www.blender.org/download/).
- [Penumbra](https://github.com/xivdev/Penumbra) and [Glamourer](https://github.com/Ottermandias/Glamourer) for a modded appearance.
- The original Meddle plugin disabled while XivBlend is loaded. XivBlend contains its own Meddle-derived extraction engine and both must not hook the same client simultaneously.

## Install

### Plugin repository

Add this URL under `/xlsettings` → **Experimental** → **Custom Plugin Repositories**, then install **XivBlend Prototype** through `/xlplugins`:

```text
https://raw.githubusercontent.com/kittenhaswares-ui/XivBlend/main/repo.json
```

### Manual test build

1. Download `latest.zip` from the [latest release](https://github.com/kittenhaswares-ui/XivBlend/releases/latest).
2. Extract it into its own folder and keep all DLLs together.
3. Under `/xlsettings` → **Experimental** → **Dev Plugin Locations**, add the exact extracted `XivBlend.Plugin.dll`, not the containing folder.
4. Enable **XivBlend Prototype** in `/xlplugins` if it did not load automatically.

See [Dalamud's development-plugin guidance](https://dalamud.dev/faq/getting-started/) for the Dev Plugin Locations interface.

## Export your character

1. Finish the appearance in Glamourer and Penumbra.
2. Exit Group Pose.
3. Use Penumbra's redraw function and wait until the character is fully visible.
4. Run `/xivblend`.
5. If Blender was not detected automatically, press **Browse...** and select `blender.exe`.
6. Press **Export My Character to Blender**.

The result is created under:

```text
Documents\XivBlend Exports\XivBlend-<character>-<time>\
```

That folder contains the packed `.blend`, the intermediate glTF and cache, `xivblend-manifest.json`, `xivblend-export-report.json`, and the Meddle snapshot. Keep those diagnostics while the exporter is experimental.

Diagnostics can contain the character name and local mod paths. Review and redact them before posting a bug report. Never upload paid mod files.

## Verified so far

- The Dalamud API 15 / .NET 10 release build completes with zero warnings and errors.
- The Blender worker and pinned MeddleTools assets are embedded inside the plugin DLL and extract without traversal or cache artifacts.
- Blender 5.0 imports and reopens a rigged body-and-clothing fixture.
- The reopened scene retains its armature, bound body/clothes meshes, mapped materials, packed textures, camera, light, and embedded manifest.
- The Windows x64 release package carries the plugin dependencies and visible AGPL license/notice files.

The remaining important validation is real-world live FFXIV exports across different races, bodies, equipment, and mod combinations. Game updates can also change the native structures used by the extractor.

## Prototype limitations

- No animation Actions or FFXIV animation library yet.
- No cloth/hair physics, material animation, VFX, or sound conversion.
- Mounts, ornaments, companions, and hidden weapons are excluded.
- Materials are close translations of FFXIV shader data, not pixel-identical copies of the game renderer.
- A Penumbra redraw is currently manual.
- This is an unofficial third-party tool. Keep extracted game and mod assets local and do not redistribute game files or paid mod assets.

## Build from source

The repository is pinned to .NET 10 and Dalamud API 15. With a current Dalamud development environment installed:

```powershell
dotnet build Meddle.sln -c Release
```

The installable package is generated below the plugin's `bin\Release` directory as `XivBlend.Plugin\latest.zip`.

The main prototype integration is in:

- `Meddle/Meddle.Plugin/Services/QuickBlendExportService.cs`
- `Meddle/Meddle.Plugin/UI/QuickBlendExportTab.cs`
- `Meddle/Meddle.Plugin/XivBlendBuilder/`

## Source and license

XivBlend is an AGPL-3.0-compatible fork of [PassiveModding/Meddle](https://github.com/PassiveModding/Meddle) at commit `312ad2610b74083376838964f5aebe6b5886449b` (v0.1.55.0). Its bundled shader conversion code is based on [PassiveModding/MeddleTools](https://github.com/PassiveModding/MeddleTools) at commit `fc241c595996321cbb4c33a87d9e299ab9d3a0cd` (v0.1.10).

See [LICENSE.txt](LICENSE.txt) and [NOTICE-XIVBLEND.md](NOTICE-XIVBLEND.md) for the complete attribution and corresponding-source information.
