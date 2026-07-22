# XivBlend architecture

This document explains where XivBlend ends, where its Meddle foundation begins, and why some apparently old names remain. It is intended for maintainers; the normal user workflow belongs in the [README](../README.md).

## Product boundary

XivBlend is not merely a renamed Meddle window. It combines Meddle's proven live-character extraction engine with a new, deliberately narrow product workflow:

```text
Dalamud LocalPlayer
  -> live draw object after Penumbra/Glamourer
  -> Meddle character/model/material extraction
  -> XivBlend manifest and provenance grouping
  -> glTF plus bounded sidecar data
  -> headless Blender 5.x builder
  -> packed, validated .blend

Selected emote icon
  -> local catalog/request queue
  -> PAP + TMB face/prop/AVFX bundle
  -> temporary Blender preview
  -> removed again before the .blend is saved
```

The self-only rule is enforced at the start of the main export path: `QuickBlendExportService` captures Dalamud's logged-in `LocalPlayer`. There is no arbitrary object-index picker or target-character UI.

## XivBlend product layer

The following areas are XivBlend-specific orchestration or substantial product work:

- `QuickBlendExportService` reserves a unique timestamped output directory, captures the self-only snapshot, runs extraction, invokes Blender, and reports the result.
- `CharacterPartProvenanceService` converts Penumbra resource information into readable collection labels such as `Hair - <Mod Name>` without renaming imported mesh datablocks.
- `AnimationLibraryService`, `VanillaPapAnimationExporter`, `AnimationPropAssetExporter`, `AnimationVfxAssetExporter`, and `AvfxStaticMeshPreviewExporter` build bounded, on-demand player-emote bundles.
- `PenumbraAnimationModService` imports explicitly selected active PAP overrides from one installed Penumbra mod.
- `BlenderAnimationBrowserInstaller` installs the reviewed Blender panel and its pinned material runtime.
- `XivBlendBuilder/build_character.py` imports the glTF, maps FFXIV materials, organizes collections, builds the portrait studio, stores provenance, packs resources, and refuses incomplete scenes.
- `XivBlendBuilder/xivblend_animation_browser` supplies the icon browser, synchronized temporary previews, effect inspection, camera tools, and Render Studio.
- `QuickBlendExportTab` and `AnimationLibraryTab` are the small user-facing Dalamud surface.

These pieces are kept separate from low-level extraction so the product workflow can evolve without rewriting the format readers and live-object composition engine.

## Retained Meddle foundation

XivBlend intentionally retains Meddle code that is still part of the working export chain. `CharacterComposer`, `ComposerCache`, and `MaterialComposer` produce the rigged glTF and its materials. The now-small `ResolverService` captures only the logged-in character and visible weapon draw objects. `ParseMaterialUtil`, `SkeletonUtils`, `PbdHooks`, and the dye-table-only `StainProvider` supply the data that capture needs. `Meddle.Utils` retains the file readers, texture/model conversion, SqPack access, and glTF helpers used by both character and prop export.

The old Meddle world, layout, housing, terrain, material-inspector, live-character preview, and debug UI chains have now been removed. Their supporting `LayoutService`, `InstanceComposer`, preview texture cache, and obsolete world-file readers were removed with them. Low-level readers that are still on the character-export path remain even when their names predate XivBlend. Clean-up follows dependency evidence rather than filename aesthetics.

The measured inheritance and deletion audit is recorded in [Code Audit](CODE_AUDIT.md).

### Why namespaces still say `Meddle.Plugin`

The plugin assembly is named `XivBlend.Plugin`, but much of the C# namespace remains `Meddle.Plugin`. Renaming every inherited type would create a large, noisy diff, make upstream comparison harder, complicate attribution, and risk breaking serialization or generated interop for no user benefit. Namespace text does not mean the original Meddle plugin is also running. The original plugin must still be disabled because both products contain the same kind of live extraction hooks.

## Rig and coordinate convention

The default `.blend` preserves Blender's glTF-imported rest matrices, inverse bind transforms, weights, and animation basis. It also stores the original glTF joint-axis correction for attachment bones. This convention is required by captured poses, on-demand animation Actions, and FFXIV item attachment transforms.

Compared with common NFLB/TexTools authoring rigs, the displayed local bone axes can look rotated: the XivBlend/glTF basis is related to the NFLB basis by a handedness-preserving 90-degree local rotation. It is **not** safe to fix this by simply swapping X and Y. A raw swap is a reflection, and changing only bone roll or display settings does not rebake animations, bind matrices, sockets, and child transforms.

The current render rig therefore remains glTF-native. A future optional **NFLB/Modding Rig** mode would need a complete conversion: rebuild the authoring rest pose, preserve the desired tail/connected-bone policy, rebind meshes, and rebake every body/face Action and attachment transform into the new world-space basis. That should be an explicit export mode, never a silent change to the render rig.

## Blender scene and render presets

Builder **0.10.2** creates a 1440 x 1800 portrait scene, fitted camera, grounded sweep, and studio lights. Browser **0.9.0** exposes five direct Blender workflows:

- **Animate:** Solid viewport for responsive posing. It does not replace or edit character materials.
- **Preview:** Eevee with real materials for fast shaded checks.
- **Beauty:** Cycles with adaptive sampling, denoising, conservative ordinary light paths, 128 transparent bounces for deeply layered alpha-card hair and fur, and the user's available Cycles device.
- **Detail:** the same Cycles quality and materials with a tighter key, reduced fill and world light, a controlled rim, and deeper surface-defining shadows.
- **Mood:** a true side-lit portrait arrangement with a small warm key, near-zero ambient/front fill, a cool rear kicker, and strong Rembrandt-style shadows.

The background choices are Charcoal Brand, Neutral Gray, and Transparent. The color choices are Beauty (AgX) and Accurate Mod Colors (Khronos PBR Neutral with neutral lights). Output choices are Web PNG (8-bit), High Quality PNG (16-bit), and a half-float EXR editing master.

The sweep's visible gradient is camera-only in Cycles so it does not act like a giant emission light and flatten the character. The character receives shaped key/fill/rim lighting; the rim can be linked to the character collection. Cycles uses a 2K viewport texture limit without destructively converting source textures. No third-party “photoreal filter” or paid add-on is required. The Beauty result remains reproducible and suitable for consistent mod previews.

Final Cycles modes temporarily lower only an unlinked Principled subsurface-weight socket on materials positively identified as `skin.shpk` face materials. All source color, normal, tiled pore detail, roughness, specular, makeup, and mod texture links remain unchanged. The browser records the actual socket value, restores it for Animate/Preview, restores every active material before saving or unregistering, and reapplies the final-mode profile after a successful save or file load. Linked inputs and body skin are skipped.

## Animation storage and schemas

Game assets are not bundled in the plugin or Blender add-on. The plugin builds a versioned local catalog and serves only the selected request. Runtime Actions, facial NLA layers, props, and effects are removed before save, keeping character files smaller and avoiding accidental asset embedding.

Current format layers are:

| Data | Schema/version |
| --- | --- |
| Character snapshot manifest | Schema 3 |
| Animation catalog | Schema 2 |
| Animation request queue | Schema 2 |
| Animation bundle | Schema 3 |
| Stored glTF bone-axis correction | Schema 1 |
| Blender character builder | 0.10.2 |
| Blender animation browser | 0.9.0 |

These versions are intentionally independent of the Dalamud plugin release number. A plugin update can change the builder or browser without changing every data schema, and a schema can change when compatibility requires it. Code should validate the layer it consumes instead of treating the plugin version as a universal format version.

## Privacy model

The `.blend` stores limited, redacted provenance so collections remain useful. It is not guaranteed anonymous because character, material, texture, and mod-derived names may remain visible.

The working sidecars are explicitly local diagnostics, not share-ready output. The snapshot manifest is schema 3 and can include character identity, encoded Glamourer state, Penumbra-resolved paths, absolute mod paths, and asset caches. Each export gets a new timestamped folder and old folders are never silently removed. This makes failures diagnosable and avoids overwrites, but users must review diagnostics before sharing and manage disk use themselves.

XivBlend must not be used as an asset redistribution tool. Extracted FFXIV data, paid mods, and other creators' files stay subject to their original rights and permissions.

## Current limitations

- Only the logged-in character is exported; mounts, ornaments, companions, hidden weapons, NPCs, and targets are outside scope.
- Player emotes and expressions are on demand. Combat and weapon actions, sound, camera events, mounts, and alternate timeline slots are excluded.
- Timed body and face layers, real model props, and AVFX metadata are supported, but some auxiliary ear/tail/part tracks are not yet merged.
- Blender does not reproduce the Apricot particle runtime, trails, distortion, collision, or FFXIV's exact game shaders. Static embedded AVFX geometry is inspection data, not equivalent playback.
- Separate prop animation, physics, unusual attachment classes, cloth/hair simulation, and material animation may require event-specific work.
- Common-race fallback animation uses named-bone retargeting, and extracted root motion is not yet reconstructed.
- Materials are careful translations of FFXIV shader data, not pixel-identical copies of the game renderer.
- A Penumbra redraw remains a manual pre-export step.
- Game updates can change native structures and require new validation.

## Source and licensing

XivBlend is an AGPL-3.0-compatible fork of PassiveModding's Meddle. Its pinned material conversion runtime is based on MeddleTools, and bounded PAP/SKLB/TMB work includes MIT-licensed VFXEditor-derived code. Exact upstream commits and notices are recorded in [NOTICE-XIVBLEND.md](../NOTICE-XIVBLEND.md); the repository license is in [LICENSE.txt](../LICENSE.txt).
