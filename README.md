# XivBlend

XivBlend is an experimental Dalamud plugin that exports **your own currently displayed FFXIV character** into one packed Blender file.

The one-button prototype reads the final live draw object after Glamourer and Penumbra have applied the character's appearance. It exports the body, face, hair, visible equipment and weapons, materials, textures, morphs, skin weights, and deformation rig. Blender is then launched headlessly to build and save the `.blend`.

> **Prototype status:** version 0.0.6 adds an optional, on-demand Blender browser for 279 vanilla player emotes and facial expressions in the current validated game data. It keeps the catalog and decoded clips in a shared local cache rather than bloating each `.blend`. The character exporter has passed release-build, Blender-reopen, and offline end-to-end checks; the new live first-click animation bridge still needs broad in-game coverage.

## What it exports

- Only Dalamud's logged-in `LocalPlayer`; there is no target or nearby-player selector.
- Body, face, hair, visible clothing, accessories, and visible weapons.
- Final Penumbra-resolved model, material, and texture data from the live draw object.
- Glamourer state and Penumbra resource-path diagnostics when their IPC APIs are available.
- Skeleton, skin weights, morphs, material colorsets, custom character colors, and textures.
- A Blender 5.x scene with a clean stick rig, organized collections, a portrait camera, removable studio backdrop, three-point lighting, mapped FFXIV materials, packed images, redacted provenance, an embedded build report, and a script-free A-pose/captured-pose Timeline slider.
- When explicitly set up, an asset-free Blender sidebar that shows locally extracted game emote icons and requests only the selected vanilla skeletal animation. Combat, weapons, VFX, props, and modded animations are excluded.

XivBlend rejects an export as incomplete when a visible model or material fails, or when Blender cannot verify a mesh bound to the imported armature.

## Requirements

- Windows x64 with FFXIV launched through XIVLauncher/Dalamud API 15.
- [Blender 5.x](https://www.blender.org/download/).
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

Every click creates a new timestamped folder. Earlier exports are never overwritten or deleted, so repeated exports can use substantial disk space. After checking that a packed `.blend` opens correctly, remove old export folders manually when you no longer need their diagnostics.

The `.blend` contains redacted provenance, but it is not anonymous: its filename, rig, material, and texture names may still identify the character or mods. The sidecars—especially `xivblend-manifest.json`, `*-meddle.json`, glTF/bin, and `cache`—can contain the character name, raw Glamourer state, absolute mod paths, and mod assets. Do not upload the whole folder. Review and redact anything shared in a bug report, and never upload paid mod files.

## Set up the animation browser

1. Open XivBlend's **Animations** tab in FFXIV.
2. Press **Set Up / Update Animation Browser** and wait for the catalog and Blender-panel status messages to finish.
3. Export the character again with version 0.0.6; older `.blend` files lack the race and face-skeleton lookup metadata.
4. Restart Blender if it was already open. In the 3D View press `N`, open **XivBlend** → **Player Emotes**, then click an icon.
5. Keep FFXIV running with XivBlend loaded for the first click on an uncached clip. Later clicks use the shared local cache.
6. Use **Stop / Restore Captured Pose** to unload the preview and return to the exported pose.

The library is intentionally limited to the primary icon-click animation for 279 built-in player emotes and expressions in the current validated game data. It does not read Penumbra animation mods or include combat actions, weapons, VFX, props, mounts, alternate timeline slots, or the complete TMB animation layering. It reads canonical paths from the live SqPack; current Dalamud cannot independently prove that a live index was never modified by TexTools, so restore TexTools index changes first. Preview Actions are removed before saving, so game animation data is not embedded in the `.blend`.

See [Animation browser workflow, scope, and limitations](docs/ANIMATION_LIBRARY.md) for cache behavior, update steps, technical limits, and local-extraction boundaries.

## Using the generated Blender file

- The file opens on Timeline frame 100, labeled `CAPTURED POSE`. Drag the ordinary Blender Timeline to frame 0, labeled `XIV A-POSE`, for the rig's standard rest A-pose; frames between them blend linearly. This control is stored as normal keyframes and requires no add-on, embedded script, or trusted-script permission.
- The apparent blocky rig in earlier exports was caused by glTF-imported Icosphere custom bone shapes overriding Blender's `STICK` display. New files disable those widgets and show the clean stick armature by default.
- Viewport grid, coordinate axes, relationship lines, camera, and light helpers are hidden by default. The camera and three studio lights remain active for F12 renders.
- Native glTF rest matrices, bind transforms, and bone axes are preserved; the pose slider does not remap the rig. For an FBX round trip, use Primary Bone Axis `X` and Secondary Bone Axis `Y` at the FBX export/import boundary only.
- Existing `.blend` files are not retroactively changed. Export again with 0.0.6 to receive the rig metadata required by the animation browser.

## Verified so far

- The Dalamud API 15 / .NET 10 release build completes with zero warnings and errors.
- The Blender worker and pinned MeddleTools assets are embedded inside the plugin DLL and extract without path traversal or embedded Python bytecode/cache artifacts.
- Blender 5.0 imports and reopens a rigged body-and-clothing fixture.
- The reopened scene retains its armature, bound body/clothes meshes, mapped materials, packed textures, portrait camera, render-active studio lights/backdrop, redacted provenance, embedded build report, clean viewport defaults, and the frame 0-to-100 pose slider.
- The strict current-game catalog filter resolves 279 primary player-emote/expression entries (150 General, 100 Special, and 29 Expressions) while excluding weapon timelines, non-player timelines, and commandless internal/event variants.
- The animation catalog, icons, request queue, and decoded clips are versioned outside character files; the plugin and Blender add-on contain no bundled FFXIV assets.
- The Windows x64 release package carries the plugin dependencies and visible AGPL license/notice files.

The remaining important validation is real-world live FFXIV exports and first-click animation decoding across different races, face rigs, equipment, and mod combinations. Game updates can also change the native structures used by the extractor.

## Prototype limitations

- The animation browser includes only vanilla player emotes and facial expressions, primary slot zero. It excludes combat, weapons, VFX, sound, props, mounts, NPC/modded animations, and alternate timeline slots.
- Multi-clip PAP auxiliary layers and TMB orchestration are not merged yet, so some ear/tail/part motion can be absent; the deterministic main-track fallback still needs full-catalog auditing.
- Common c0101 fallback clips use simple named-bone retargeting in Blender, and extracted reference-frame root motion is not reconstructed yet.
- No cloth/hair physics or material-animation conversion.
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
- `Meddle/Meddle.Plugin/Services/AnimationLibraryService.cs`
- `Meddle/Meddle.Plugin/Services/VanillaPapAnimationExporter.cs`
- `Meddle/Meddle.Plugin/UI/QuickBlendExportTab.cs`
- `Meddle/Meddle.Plugin/UI/AnimationLibraryTab.cs`
- `Meddle/Meddle.Plugin/XivBlendBuilder/`

## Source and license

XivBlend is an AGPL-3.0-compatible fork of [PassiveModding/Meddle](https://github.com/PassiveModding/Meddle) at commit `312ad2610b74083376838964f5aebe6b5886449b` (v0.1.55.0). Its bundled shader conversion code is based on [PassiveModding/MeddleTools](https://github.com/PassiveModding/MeddleTools) at commit `fc241c595996321cbb4c33a87d9e299ab9d3a0cd` (v0.1.10). The local Havok PAP/SKLB loading and sampling implementation is adapted from the MIT-licensed [Dalamud VFXEditor](https://github.com/0ceal0t/Dalamud-VFXEditor) at commit `cd878d0e029d515acef723494ea4ffe5dbe19ade`.

See [LICENSE.txt](LICENSE.txt) and [NOTICE-XIVBLEND.md](NOTICE-XIVBLEND.md) for the complete attribution and corresponding-source information.
