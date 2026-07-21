# XivBlend

XivBlend is an experimental Dalamud plugin that exports **your own currently displayed FFXIV character** into one packed Blender file.

The one-button prototype reads the final live draw object after Glamourer and Penumbra have applied the character's appearance. It exports the body, face, hair, visible equipment and weapons, materials, textures, morphs, skin weights, and deformation rig. Blender is then launched headlessly to build and save the `.blend`.

> **Prototype status:** version 0.0.13 turns each clicked body emote into an on-demand bundle: the primary PAP, exact TMB-scheduled facial clips, real TMB-spawned prop models, and native AVFX metadata share the game's 30 fps clock. Props use the locally installed game model, material, textures, colorset, hand, scale, and lifetime. Native `.avfx` sources are cached with their exact timing and placement, while effects that require FFXIV's Apricot particle runtime are identified instead of being replaced by fake Blender objects. A selected Penumbra animation mod can also contribute active overrides and standalone player pose/loop PAPs as clearly labeled **Custom** cards. The shared cache remains outside every `.blend`, and preview Actions/props are removed before saving.

## What it exports

- Only Dalamud's logged-in `LocalPlayer`; there is no target or nearby-player selector.
- Body, face, hair, visible clothing, accessories, and visible weapons.
- Final Penumbra-resolved model, material, and texture data from the live draw object.
- Glamourer state and Penumbra resource-path diagnostics when their IPC APIs are available.
- Skeleton, skin weights, morphs, material colorsets, custom character colors, and textures.
- A Blender 5.x scene with a clean stick rig, organized collections, a 1440×1800 portrait camera, grounded seamless backdrop, softbox-style three-point lighting, mapped FFXIV materials, packed images, redacted provenance, an embedded build report, and a script-free A-pose/captured-pose Timeline slider.
- When explicitly set up, a Blender sidebar that ships without game assets, shows locally extracted emote icons, and requests only the selected synchronized animation bundle. It supports vanilla body/face timing, exact on-demand prop assets, native AVFX inspection metadata, and explicitly imported active or standalone Penumbra PAPs. Combat, weapon actions, mounts, and automatic bulk extraction remain excluded.

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
3. Export the character again with version 0.0.6 or newer; older `.blend` files lack the race and face-skeleton lookup metadata.
4. Restart Blender if it was already open. In the 3D View press `N`, open **XivBlend** → **Player Emotes**, then click an icon.
5. Keep FFXIV running with XivBlend loaded for the first click on an uncached clip. Later clicks use the shared local cache.
6. Use **Stop / Restore Captured Pose** to unload the preview and return to the exported pose.

For a custom dance or other animation mod, press **Refresh Penumbra Mods**, choose the mod, then press **Add Active Animation Overrides**. XivBlend reads the selected mod's bounded Penumbra manifests, asks Penumbra which discovered player-body PAPs are currently winning for the local player, records their selected option names and content hashes, and adds separate cards under **Custom**. Canonical emote replacements retain their base-emote label; standalone player pose/loop PAPs receive their own cards even when they do not replace a catalog entry. Changing the mod file or its options requires importing it again. Removing a saved source changes only XivBlend's catalog, never the Penumbra mod.

The library is intentionally limited to the primary icon-click animation for 279 built-in player emotes and expressions in the current validated game data. Body-emote TMB schedules add exact facial clips and real spawned prop assets. Native AVFX files are hash-verified and classified on demand; embedded static draw geometry can be exported for inspection, but full Apricot particle playback is not yet reproduced in Blender. Combat actions, weapons, mounts, alternate timeline slots, sound, and camera events remain excluded. Vanilla assets come from canonical live SqPack paths; current Dalamud cannot independently prove that a live index was never modified by TexTools, so restore TexTools index changes first. Preview Actions and generated prop objects are removed before saving, so game animation data is not embedded in the `.blend`.

See [Animation browser workflow, scope, and limitations](docs/ANIMATION_LIBRARY.md) for cache behavior, update steps, technical limits, and local-extraction boundaries.

## Using the generated Blender file

- The file opens on Timeline frame 100, labeled `CAPTURED POSE`. Drag the ordinary Blender Timeline to frame 0, labeled `XIV A-POSE`, for the rig's standard rest A-pose; frames between them blend linearly. This control is stored as normal keyframes and requires no add-on, embedded script, or trusted-script permission.
- New exports open with the portrait camera fitted to the actual captured pose rather than artificial frames between the A-pose and capture. The character stands on the sweep instead of floating above it. A warm rectangular key models the form, low cool fill preserves shadows, a cool strip rim separates hair and clothing, and only the key casts shadows. AgX highlight control, a 96-sample Eevee preset, and 16-bit PNG output are ready for a clean still render.
- With animation-browser version 0.5.0 installed, open the 3D View's `N` sidebar and expand **XivBlend** → **Render Studio**. Use **Smooth Animation** for a deliberately simplified clay viewport driven by one shared shader, then **Full Detail** for the exact exported appearance. F12/Render Portrait output and saving automatically switch to Full Detail and safely resume Smooth Animation afterward. On the validated 61-mesh c0801 export, Smooth Animation sustained 30 fps at a 1845×1158 Rendered viewport; results still depend on the scene and other GPU workloads.
- **Fit Camera to Current Pose** gives the strongest composition for one frame; **Fit Camera to Whole Animation** measures up to 96 evenly spaced poses across the active clip to keep its motion inside the shot; **Render Portrait** opens Blender's ordinary render view. Camera fitting does not change the lens, animation, or current frame.
- The apparent blocky rig in earlier exports was caused by glTF-imported Icosphere custom bone shapes overriding Blender's `STICK` display. New files disable those widgets and show the clean stick armature by default.
- Viewport grid, coordinate axes, relationship lines, camera, and light helpers are hidden by default. The camera and three studio lights remain active for F12 renders.
- Native glTF rest matrices, bind transforms, and bone axes are preserved; the pose slider does not remap the rig. For an FBX round trip, use Primary Bone Axis `X` and Secondary Bone Axis `Y` at the FBX export/import boundary only.
- Existing `.blend` files are not retroactively relit or repaired for unnecessary opaque dither. Export again with 0.0.11 or newer for the new studio and material cleanup. The 0.5.0 viewport, animation-bundle, camera, and render controls work on compatible metadata-bearing XivBlend files after reinstalling the Blender panel.

## Verified so far

- The Dalamud API 15 / .NET 10 release build completes with zero warnings and errors.
- The Blender worker and pinned MeddleTools assets are embedded inside the plugin DLL and extract without path traversal or embedded Python bytecode/cache artifacts.
- Blender 5.0 imports and reopens a rigged body-and-clothing fixture.
- The reopened scene retains its armature, bound body/clothes meshes, mapped materials, packed textures, captured-pose portrait camera, render-active grounded studio lights/backdrop, redacted provenance, embedded build report, clean viewport defaults, and the frame 0-to-100 pose slider.
- Source-declared opaque materials have no live Principled alpha link or transparent shadow path; true transparent, clipped, dithered, and unknown mod materials remain untouched.
- The Smooth Animation mode restores original material datablock identities, scene quality settings, and viewport state exactly, and its runtime preview material is never saved into the character file.
- The strict current-game catalog filter resolves 279 primary player-emote/expression entries (150 General, 100 Special, and 29 Expressions) while excluding weapon timelines, non-player timelines, and commandless internal/event variants.
- A read-only audit of all 250 body entries in the current catalog parsed without failures: 210 declare face libraries, 736 face events resolved to 437 exact facial clips on the validated face rig, and 149 VFX plus 55 prop events remained synchronized and bounded.
- A focused current-game c0801 audit found real visible AVFX on 52 emotes and real model props on 51. All 66 unique non-sync player-emote AVFX sources parsed; 33 contain validated embedded static draw geometry and 33 require the Apricot runtime for any meaningful preview.
- Synchronized animation-bundle loading, timed facial NLA layers, exact apple import/material binding, transient cleanup, and Action-slot binding pass headless tests in Blender 5.0 and Blender 5.2. The catalog, request queue, and bundle manifests use schema 2.
- Custom animation imports use Penumbra's physical mod-directory API and effective local-player collection, accept only winning PAP files contained inside the chosen mod's final filesystem path, and recheck the size, content hash, and path before decoding. Bounded manifest discovery and standalone player pose/loop imports pass the utility test suite.
- The animation catalog, icons, request queue, and decoded clips are versioned outside character files; the plugin and Blender add-on contain no bundled FFXIV assets.
- The Windows x64 release package carries the plugin dependencies and visible AGPL license/notice files.

The remaining important validation is real-world live FFXIV exports and first-click animation decoding across different races, face rigs, equipment, and mod combinations. Game updates can also change the native structures used by the extractor.

## Prototype limitations

- The animation browser includes player emotes and facial expressions at primary slot zero, plus explicitly selected active Penumbra body-PAP overrides. It excludes combat, weapon actions, sound, mounts, NPC animations, and alternate timeline slots.
- TMB-scheduled facial clips are reconstructed, but other auxiliary skeletal layers such as some ear/tail/part tracks are not yet merged.
- TMB-spawned model props are exact local game assets, but their separate spawned-object animation, physics, or unusual attachment behavior may still need event-specific support. Native AVFX sources and placement metadata are extracted, but Blender does not yet simulate Apricot emitters, particles, trails, distortion, collision, or game shaders; static embedded geometry is inspection data, not equivalent playback. Custom face-PAP replacements and mod-supplied VFX assets are not imported yet.
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

XivBlend is an AGPL-3.0-compatible fork of [PassiveModding/Meddle](https://github.com/PassiveModding/Meddle) at commit `312ad2610b74083376838964f5aebe6b5886449b` (v0.1.55.0). Its bundled shader conversion code is based on [PassiveModding/MeddleTools](https://github.com/PassiveModding/MeddleTools) at commit `fc241c595996321cbb4c33a87d9e299ab9d3a0cd` (v0.1.10). The local Havok PAP/SKLB loading, sampling, and bounded TMB timeline reader are adapted from the MIT-licensed [Dalamud VFXEditor](https://github.com/0ceal0t/Dalamud-VFXEditor) at commit `cd878d0e029d515acef723494ea4ffe5dbe19ade`.

See [LICENSE.txt](LICENSE.txt) and [NOTICE-XIVBLEND.md](NOTICE-XIVBLEND.md) for the complete attribution and corresponding-source information.
