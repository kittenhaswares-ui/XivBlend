# XivBlend animation browser

XivBlend 0.0.15 provides an on-demand Blender browser for a deliberately narrow set of player emotes. A click builds one synchronized local bundle: the primary skeletal PAP, the facial clips scheduled by its TMB, real spawned prop assets, and native AVFX metadata all use FFXIV's 30 fps timeline. The browser can also add active overrides and standalone player pose/loop PAPs from one explicitly selected Penumbra mod. It is not a bulk game-asset extractor, and generated game/mod data stays in a shared local cache rather than inside character `.blend` files.

## Scope

The current validated `Emote` sheet produces 279 primary icon-click entries:

| Category | Entries |
| --- | ---: |
| General | 150 |
| Special | 100 |
| Expressions | 29 |
| **Total** | **279** |

An entry must have a name, icon, text command, player-emote category, and usable slot-zero `ActionTimeline`. Weapon-drawing rows and `/draw` or `/sheathe` are rejected. The accepted timeline must be either a common body animation or a `facial/pose/` expression.

Included:

- The primary body or expression associated with the normal emote icon click.
- Race-specific body PAPs when present, with the common `c0101` PAP as the normal named-bone fallback.
- The face skeleton actually captured on the exported character, such as `f0002`.
- Exact TMB facial timing. `TMPP` identifies the preferred face library; each embedded `C010` selects and schedules its exact `cfxf_*` clip. Clip-derived and resident packs provide bounded fallbacks for vanilla timelines that omit or mix libraries.
- Exact TMB model events. The referenced local SqPack model, material, textures, colorset, hand flag, attachment transform, scale, and frame lifetime are exported on demand. New character exports also retain the original glTF basis for attachment bones, correcting Blender's bone-axis presentation before the game's ATCH transform is applied to supported item props.
- Native AVFX event data. Exact `.avfx` sources are hash-verified in the local cache with TMB timing, color, scale, and placement metadata. Embedded static draw geometry can be converted with a neutral unlit, double-sided inspection material, while Apricot-dependent effects are explicitly identified.
- Explicitly imported Penumbra player-body PAPs winning for the local player's effective collection. Canonical replacements are labeled with the base emote; standalone player pose/loop paths receive their own **Custom** cards.
- The game's high-resolution emote icons, extracted locally for the Blender browser.

Excluded:

- Combat/job actions, weapon timelines, sheathe/draw actions, mounts, movement sets, ornaments, companions, and NPC-only animations.
- Alternate emote slots such as intro, ground, chair, and upper-body variants.
- Sound, camera events, separate spawned-object animation/physics, and Apricot AVFX particle simulation. Static embedded AVFX geometry is inspection-only and is not automatically shown as if it were the complete effect.
- Custom face-PAP replacements and mod-supplied VFX assets. A custom body PAP can schedule ordinary vanilla facial clips, but custom facial libraries are not trusted/imported in this milestone.
- Automatic extraction or embedding of the entire catalog.

The filter is data-driven, so a future game update can change the count without broadening these exclusions.

## How the bridge works

1. XivBlend builds the vanilla catalog from canonical Excel, icon, TMB, PAP, and SKLB paths in the user's live installation. Penumbra redirections are not used for vanilla cards.
2. **Set Up / Update Animation Browser** writes the asset-free Blender add-on, catalog JSON, and locally converted icon PNGs.
3. The generated character `.blend` stores only safe lookup metadata: race code, captured face-skeleton token, catalog schema, and the original source basis required by supported attachment sockets.
4. Clicking an uncached card writes a local queue request. XivBlend selects the exact primary PAP track from the action TMB, samples it through FFXIV's loaded Havok runtime, then parses that selected track's embedded timeline.
5. Every distinct scheduled facial clip is sampled once. The bundle manifest places reusable face GLBs as timed Blender NLA strips, including source-frame slices and held one-frame expressions.
6. Blender applies the body Action, combines the facial strips, imports supported exact prop assets as transient objects, exposes native AVFX status/metadata and available static inspection geometry, and loops the bundle.
7. Clicking the same card for the same game build, rig, face, converter, and custom-content hash is a cache hit.
8. Before a save or reload, Blender restores the captured pose and removes runtime Actions, NLA strips, objects, collections, and materials. The live preview can resume after a successful save.

## Set up and use

1. Install XivBlend 0.0.15 or newer and select Blender 5.x in the **Export** tab if it was not detected automatically.
2. Open XivBlend's **Animations** tab in FFXIV.
3. Click **Set Up / Update Animation Browser** and wait for both status messages to finish.
4. Export a character with race and face-skeleton metadata, then restart Blender if it was already open.
5. In the 3D View press `N`, open **XivBlend**, and expand **Player Emotes**.
6. Search or filter by category, then click a card. Keep FFXIV and XivBlend running for the first uncached play.
7. Click **Stop / Restore Captured Pose** to remove the runtime bundle and return to the exported pose.

The browser targets the active armature, or the exported primary armature when no suitable rig is selected.

## Add a custom Penumbra animation

1. Enable the desired mod and option in the Penumbra collection affecting your current character. Resolve conflicts so the wanted PAP is actually winning.
2. In XivBlend's **Animations** tab, click **Refresh Penumbra Mods**.
3. Choose the mod and click **Add Active Animation Overrides**.
4. Refresh/reinstall the browser if it is already open, then use the new **Custom** category in Blender.

XivBlend obtains the chosen mod's physical directory from Penumbra, reads only bounded `default_mod.json` and `group_*.json` manifests, and discovers canonical player-body PAP paths for the exported race plus the common fallback rig. Penumbra's effective local-player collection decides whether each candidate is active and winning. A candidate is accepted only when its final filesystem path remains inside the chosen mod directory. XivBlend stores the mod identifier, selected option labels, target/source rig, relative path, byte length, and SHA-256 hash. Before every decode it asks Penumbra for the mod root again, resolves junctions/links, and verifies the same length and hash. Blender never supplies an arbitrary mod path.

This also covers standalone player pose/loop files that have no vanilla catalog key. Their labels are derived from the safe relative PAP name and selected option metadata; they do not pretend to be a built-in emote.

If the file or selected option changes, import the mod again. **Remove Saved Source from XivBlend** removes all of that mod's saved cards without changing Penumbra or deleting mod files. Imports for another player race are merged with existing race bindings instead of erasing them.

The importer has conservative PAP size, cumulative-import, animation-count, duration, bone-count, and sampled-transform limits. These bounds protect the live game process from malformed or accidentally enormous files.

## Render Studio controls

Animation-browser 0.7.0 also provides **XivBlend** → **Render Studio**:

- **Animate** switches the 3D View to Blender's lightweight Solid shading without replacing any character material.
- **Preview** uses Eevee, the real materials, and the exported studio lights for quick shaded checks.
- **Beauty** uses Cycles with adaptive sampling, denoising, and the user's configured Cycles device for final stills.
- Background presets provide the repeatable charcoal sweep, neutral gray, or transparency. Color presets provide AgX beauty color or Khronos PBR Neutral for more literal mod colors.
- **Fit Camera to Current Pose** frames the current Timeline frame.
- **Fit Camera to Whole Animation** measures up to 96 evenly spaced poses across the active bundle.
- **Render Portrait** opens Blender's normal render view with the scene's existing output settings.

The retired clay material override is removed automatically if an older file or session still contains it. Runtime animation, prop, and effect data are still removed before saving and restored afterward, so a preview does not silently bloat the character `.blend`.

## Cache and updates

The shared cache is stored under:

```text
%LOCALAPPDATA%\XivBlend\AnimationLibrary
```

Catalog builds are separated by game version, client language, and converter version. Body cache paths also include the captured face key, and custom cards include a content-derived identity. `current.json` points Blender to the active build. Icons are prepared during setup; bundles and GLBs are generated only when clicked.

The 0.0.15 compatibility line is animation browser 0.7.0, character builder 0.10.0, catalog/request schema 2, and bundle schema 3. Static AVFX inspection meshes use cache format v2; an older v1 preview is not reused and is regenerated when the relevant emote bundle rebuilds.

- Run **Refresh Game Catalog** after a game update, language change, or custom-source import.
- Use **Reinstall Blender Panel** when only the add-on needs an update.
- Use **Open Local Animation Cache** to inspect storage.
- Old versioned builds are retained; XivBlend never silently deletes them.
- Deleting the cache is recoverable: run setup again, then replay cards as needed.
- Do not distribute cache files or copy them into a shared export folder.

## Fidelity limitations

The body Action is still the primary PAP track. TMB-scheduled face clips are reconstructed, but other auxiliary skeletal layers—such as some ear, tail, part, or additive tracks—are not yet merged. Root motion, game transitions, environment interaction, and emote conditions are not reproduced.

TMB-spawned model props use the exact locally installed game mesh and mapped material data. The TMB `C198` record directly supplies its transient weapon model/body/variant triple; it is not an Item-sheet lookup, whose shared display models can identify a different asset. The TMB hand flag, frame lifetime, color, scale, and placement are preserved. For supported item props, new exports combine the original glTF socket basis with the game's ATCH transform. Compatible older XivBlend files without that metadata receive a deliberately narrow correction for known hand/throw sockets; arbitrary rigs and unusual item classes are not guessed. Some props may still need event-specific spawned-object animation, physics, or attachment support.

Native AVFX files are not treated as simple meshes. XivBlend preserves and validates the exact source plus event metadata, classifies whether the file has static embedded draw geometry, and reports the Apricot features it uses. Cache-v2 static preview GLBs use an unlit, double-sided neutral material so valid vertex color is visible instead of inheriting Blender's black metallic default. Blender does not yet implement FFXIV's Apricot scheduler, emitters, particles, billboards, trails, distortion, collision, animated curves, or game shader pipeline. A static preview therefore remains inspection data rather than complete effect playback.

The browser loops previews for inspection even when the original emote is a one-shot. Common `c0101` body fallback uses named-bone retargeting, not FFXIV's complete retargeting system, so proportion-sensitive translation still needs validation across all player rigs.

## Validation

The current catalog audit parsed all 250 body-emote primary PAP timelines with zero parser failures. It observed 210 declared face libraries, 736 scheduled face events resolving to 437 exact facial clips on the validated `c0101/f0002` rig, 149 VFX events, and 55 prop events. Eat Apple and cheer-wave timing were checked directly against their live TMB/PAP data.

Schema-2 catalog/request and schema-3 bundle handling, timed NLA evaluation, Blender 5 Action-slot handling, exact apple import and material binding, complete transient cleanup, and registration pass headless tests in Blender 5.0 and Blender 5.2. All 66 unique non-sync player-emote AVFX sources in the focused current-game audit parse: 33 contain validated embedded static draw geometry and 33 are Apricot-only. Bounded Penumbra discovery tests cover active canonical replacements, standalone pose/loop files, inactive options, and path-containment rejection. Broader live testing across every race, face rig, prop/item class, custom animation style, and future game update remains necessary.

## Vanilla verification and local-only boundary

Vanilla setup and decode requests are refused when Dalamud reports modified live SqPack files. That advisory cannot independently prove that TexTools never modified a live index; restore/reset TexTools index changes first. Penumbra does not need to be disabled for vanilla cards because canonical SqPack reads bypass it.

The explicit custom importer is a separate path: Penumbra remains the conflict and option resolver, and XivBlend accepts only the selected mod's active winning PAP files for the local player. No arbitrary folder scan is exposed to Blender.

No Square Enix icon, PAP, SKLB, generated GLB, bundle, or cache file is committed to this repository or shipped in the plugin package. FFXIV and mod data remain their respective owners' property. Keep extracted files local, do not redistribute paid mod assets, and follow the applicable FFXIV agreements.

The in-process Havok loading/sampling and bounded TMB reader are adapted from the MIT-licensed [Dalamud VFXEditor](https://github.com/0ceal0t/Dalamud-VFXEditor/tree/cd878d0e029d515acef723494ea4ffe5dbe19ade). See [NOTICE-XIVBLEND.md](../NOTICE-XIVBLEND.md) for the pinned revision and license notice.
