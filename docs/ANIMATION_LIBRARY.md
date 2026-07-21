# XivBlend animation browser

XivBlend 0.0.11 provides an optional Blender sidebar for previewing a deliberately narrow set of built-in FFXIV player animations, controlling viewport quality, and framing portrait renders. It is an on-demand local bridge, not a copy of the game's complete animation archive: the catalog and icons are prepared from the user's own live installation, and only the animation clicked in Blender is decoded. Penumbra is bypassed; see the TexTools integrity caveat below.

## Scope

The v1 catalog contains 279 primary icon-click entries from the live `Emote` sheet used for validation:

| Category | Entries |
| --- | ---: |
| General | 150 |
| Special | 100 |
| Expressions | 29 |
| **Total** | **279** |

Each entry must have a name, icon, text command, player-emote category, and a usable slot-zero `ActionTimeline`. Weapon-drawing rows and `/draw` or `/sheathe` are rejected. The accepted timeline must be either a common body animation or a `facial/pose/` expression.

Included:

- Vanilla skeletal motion for player-triggered emotes.
- Vanilla facial-expression skeletal motion.
- The primary animation associated with clicking the emote icon: `ActionTimeline[0]` only.
- The game's high-resolution emote icon, extracted locally for the Blender browser.
- Race-specific body PAPs when present, with the game's common `c0101` PAP as the normal fallback.
- The face skeleton actually captured on the exported character, such as `f0002`; it is not guessed from a customization byte.

Excluded:

- Combat actions, job actions, weapon timelines, weapons, and sheathe/draw actions.
- VFX, sound, camera effects, props, mounts, movement sets, ornaments, companions, and NPC-only animations.
- Penumbra animation replacements and PAPs outside the canonical scoped game paths. Current Dalamud cannot independently prove whether a live SqPack index was previously modified by TexTools; see **Vanilla verification and local-only boundary**.
- Alternate emote timeline slots, including intro, ground, chair, and upper-body variants.
- Automatic import of the whole animation library into a character `.blend`.

The filter is data-driven, so a future game update can change the count. Refreshing the catalog never broadens the exclusions above.

## How the on-demand bridge works

1. In FFXIV, XivBlend reads the canonical Excel, icon, PAP, and SKLB paths directly from the installed live game SqPacks. Penumbra redirections are not consulted.
2. **Set Up / Update Animation Browser** writes an asset-free Blender add-on, a small catalog, and locally converted icon PNGs.
3. The generated character `.blend` stores only safe lookup metadata: its race code, captured face-skeleton token, and catalog schema. It does not contain the catalog, icons, PAPs, or the shared cache path.
4. Clicking an uncached icon in Blender writes a local request. While FFXIV and the XivBlend plugin are running, XivBlend loads the requested PAP and matching source skeleton through the game's Havok runtime, samples the clip at 30 fps, and writes one animation-only GLB.
5. Blender imports that GLB as a temporary Action, maps its named bone channels to the exported rig, and loops it.
6. Clicking the same animation for the same rig later is a cache hit and does not require the game to decode it again.

Runtime Actions are intentionally transient. Before Blender saves, the add-on restores the exported captured-pose Action and removes the preview Action from the data being written. After the save completes, the preview can resume in the open session. This keeps character files small and makes them reopen like ordinary XivBlend exports.

## Set up and use

1. Install XivBlend 0.0.11 or newer and select Blender in XivBlend's **Export** tab if it was not detected automatically.
2. Open XivBlend's **Animations** tab in FFXIV.
3. Click **Set Up / Update Animation Browser** and wait for both the game catalog and Blender panel to report success.
4. Export the character again with XivBlend 0.0.6 or newer. Older `.blend` files do not contain the race and face-rig metadata required by the browser.
5. Restart Blender if it was open while the panel was installed, then open the new `.blend`.
6. In Blender's 3D View, press `N`, open **XivBlend**, and expand **Player Emotes**.
7. Search or filter by category, then click an icon to play it on a loop.
8. For the first click on an uncached clip, keep FFXIV running with XivBlend loaded until Blender reports that the animation is playing.
9. Click **Stop / Restore Captured Pose** to unload the preview and return to the pose exported at frame 100.

The browser targets the active armature, or the exported primary armature when no suitable rig is active.

## Render Studio controls

Animation-browser version 0.3.0 adds a second panel under **XivBlend** → **Render Studio**:

- **Smooth Animation** temporarily presents the whole active View Layer through one shared, restrained clay shader. It is meant for judging motion, silhouettes, intersections, and lighting—not texture or alpha-cutout fidelity.
- **Full Detail** restores every original material datablock and viewport quality setting exactly.
- **Fit Camera to Current Pose** frames the visible character at the current Timeline frame. Use this for the strongest still-image composition after choosing an emote pose.
- **Fit Camera to Whole Animation** measures up to 96 evenly spaced poses across the active Action. Use this before rendering a clip to keep its sampled motion inside the portrait; unusually long clips with a very brief extreme pose can still need a little manual margin.
- **Render Portrait** opens Blender's normal render view with the scene's existing output settings. It does not silently save or overwrite an image.

An F12/Render Portrait render or save while Smooth Animation is active first restores Full Detail, so the lightweight preview shader is never used in final output or stored in the `.blend`; the live preview resumes afterward. Both fitting controls preserve the camera lens, current frame, animation, and playback state. They ignore the generated studio scenery, hidden objects, dummy meshes, and custom bone widgets when measuring the character. Existing compatible XivBlend `.blend` files gain these controls after the panel is reinstalled; the refined physical studio and opaque-material cleanup require a new export with XivBlend 0.0.11 or newer.

## Cache and updates

The shared cache is stored under:

```text
%LOCALAPPDATA%\XivBlend\AnimationLibrary
```

Catalog builds are separated by FFXIV game version, client language, and XivBlend converter version. `current.json` points Blender to the active build. Icons are created during setup; animation GLBs are added only when requested.

- Run **Refresh Game Catalog** after an FFXIV update or language change. A game-version mismatch is rejected instead of silently using an old clip.
- Use **Reinstall Blender Panel** when only the add-on needs repair or an update.
- Use **Open Local Animation Cache** to inspect storage. Old versioned builds are retained so XivBlend never silently deletes user data; close Blender and FFXIV before manually removing builds you no longer need.
- Deleting the cache is recoverable. Run setup again for the catalog/icons, then click animations to recreate their clips.
- Do not move cache files into an export folder or distribute them with a `.blend`.

## Current fidelity limitations

Only the primary PAP clip is sampled. XivBlend does not yet reconstruct the game's TMB orchestration or merge auxiliary clips. It first matches the named body/face clip, then uses a deterministic first type-zero body or facial-track fallback for known naming mismatches. That fallback is not yet audited against every TMB, so multi-clip PAPs can select an imperfect main layer or omit ear, tail, part, and additive layers. Props, VFX, sound, camera events, and other TMB side effects remain outside scope.

The panel loops every preview for convenient inspection, even when the original emote is a one-shot. Root motion, transitions between timelines, emote conditions, and environment interaction are not reproduced. The animation is applied to the exported rig by bone name; custom rigs or renamed bones are not supported by this prototype.

When a race-specific body PAP is absent, the common c0101 PAP is sampled against its c0101 source skeleton and its named-bone Action is applied to the exported rig in Blender. This is a simple retarget, not the game's complete retargeting system; translated roots and proportion-sensitive motion still need validation across all 18 player race/sex rigs. Extracted reference-frame root motion is not reconstructed in v1.

This first release still needs broad live-client validation across every race, face skeleton, and catalog entry. A catalog entry being visible does not guarantee that every race-specific source clip will decode correctly.

## Vanilla verification and local-only boundary

The animation bridge refuses setup and new decoding requests if Dalamud reports modified live SqPack files. However, in the current Dalamud implementation that advisory cannot independently prove index integrity; a false value is not evidence that TexTools never modified the live installation. If TexTools has been used against the live game, restore/reset its index modifications before using the browser. Penumbra is bypassed and does not need to be disabled, and Penumbra animation mods are intentionally not read.

No Square Enix icon, PAP, SKLB, generated animation GLB, or cache file is committed to this repository or shipped in the plugin package. The Blender add-on contains code only and performs no network fetch. Requests, responses, icons, and decoded clips remain on the local machine.

FFXIV game data remains Square Enix property. This project does not grant permission to redistribute extracted assets, and this document is not legal advice. Keep the cache and export diagnostics private; do not attach them to bug reports, publish them in a repository, or bundle them with shared Blender files. Users remain responsible for complying with the applicable [FINAL FANTASY XIV Software License Agreement](https://support.eu.square-enix.com/rule.php?id=5383&la=2&tag=software) and [User Agreement](https://support.eu.square-enix.com/rule.php?id=5383&la=2&tag=users).

The in-process Havok loading and sampling implementation is adapted from the MIT-licensed [Dalamud VFXEditor](https://github.com/0ceal0t/Dalamud-VFXEditor/tree/cd878d0e029d515acef723494ea4ffe5dbe19ade). See [NOTICE-XIVBLEND.md](../NOTICE-XIVBLEND.md) for the pinned revision and license notice.
