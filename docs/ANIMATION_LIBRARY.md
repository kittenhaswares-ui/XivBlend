# XivBlend animation browser

XivBlend 0.0.12 provides an on-demand Blender browser for a deliberately narrow set of player emotes. A click builds one synchronized local bundle: the primary skeletal PAP, the facial clips scheduled by its TMB, and supported visible prop/VFX events all use FFXIV's 30 fps timeline. The browser can also add active body-PAP overrides from one explicitly selected Penumbra mod. It is not a bulk game-asset extractor, and the generated data stays in a shared local cache rather than inside character `.blend` files.

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
- Visible TMB event metadata. Eat Apple's model event becomes a timed lightweight apple; recognized cyalume events become colored procedural glowsticks attached to the hand bones.
- Explicitly imported, active Penumbra body-PAP overrides for the local player's effective collection. Each card is labeled with the mod and base emote name.
- The game's high-resolution emote icons, extracted locally for the Blender browser.

Excluded:

- Combat/job actions, weapon timelines, sheathe/draw actions, mounts, movement sets, ornaments, companions, and NPC-only animations.
- Alternate emote slots such as intro, ground, chair, and upper-body variants.
- Sound, camera events, exact spawned-object motion, arbitrary AVFX particle simulation, and exact prop/AVFX meshes.
- Custom face-PAP replacements and mod-supplied VFX assets. A custom body PAP can schedule ordinary vanilla facial clips, but custom facial libraries are not trusted/imported in this milestone.
- Automatic extraction or embedding of the entire catalog.

The filter is data-driven, so a future game update can change the count without broadening these exclusions.

## How the bridge works

1. XivBlend builds the vanilla catalog from canonical Excel, icon, TMB, PAP, and SKLB paths in the user's live installation. Penumbra redirections are not used for vanilla cards.
2. **Set Up / Update Animation Browser** writes the asset-free Blender add-on, catalog JSON, and locally converted icon PNGs.
3. The generated character `.blend` stores only safe lookup metadata: race code, captured face-skeleton token, and catalog schema.
4. Clicking an uncached card writes a local queue request. XivBlend selects the exact primary PAP track from the action TMB, samples it through FFXIV's loaded Havok runtime, then parses that selected track's embedded timeline.
5. Every distinct scheduled facial clip is sampled once. The bundle manifest places reusable face GLBs as timed Blender NLA strips, including source-frame slices and held one-frame expressions.
6. Blender applies the body Action, combines the facial strips, creates supported transient visual approximations, and loops the bundle.
7. Clicking the same card for the same game build, rig, face, converter, and custom-content hash is a cache hit.
8. Before a save or reload, Blender restores the captured pose and removes runtime Actions, NLA strips, objects, collections, and materials. The live preview can resume after a successful save.

## Set up and use

1. Install XivBlend 0.0.12 or newer and select Blender 5.x in the **Export** tab if it was not detected automatically.
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

XivBlend asks Penumbra for the local player's effective collection and resolves only known canonical body-emote PAP paths. A candidate is accepted only when its final filesystem path remains inside the chosen mod directory. XivBlend stores the mod identifier, selected option labels, target/source rig, relative path, byte length, and SHA-256 hash. Before every decode it asks Penumbra for the mod root again, resolves junctions/links, and verifies the same length and hash. Blender never supplies an arbitrary mod path.

If the file or selected option changes, import the mod again. **Remove Saved Source from XivBlend** removes all of that mod's saved cards without changing Penumbra or deleting mod files. Imports for another player race are merged with existing race bindings instead of erasing them.

The importer has conservative PAP size, cumulative-import, animation-count, duration, bone-count, and sampled-transform limits. These bounds protect the live game process from malformed or accidentally enormous files.

## Render Studio controls

Animation-browser 0.4.0 also provides **XivBlend** → **Render Studio**:

- **Smooth Animation** temporarily presents the active View Layer through one restrained shared material for responsive posing and motion review.
- **Full Detail** restores the original materials and viewport settings exactly.
- **Fit Camera to Current Pose** frames the current Timeline frame.
- **Fit Camera to Whole Animation** measures up to 96 evenly spaced poses across the active bundle.
- **Render Portrait** opens Blender's normal render view with the scene's existing output settings.

Rendering or saving while Smooth Animation is active first restores Full Detail. The preview resumes afterward, while the lightweight material and runtime animation/effect data remain absent from the saved `.blend`.

## Cache and updates

The shared cache is stored under:

```text
%LOCALAPPDATA%\XivBlend\AnimationLibrary
```

Catalog builds are separated by game version, client language, and converter version. Body cache paths also include the captured face key, and custom cards include a content-derived identity. `current.json` points Blender to the active build. Icons are prepared during setup; bundles and GLBs are generated only when clicked.

- Run **Refresh Game Catalog** after a game update, language change, or custom-source import.
- Use **Reinstall Blender Panel** when only the add-on needs an update.
- Use **Open Local Animation Cache** to inspect storage.
- Old versioned builds are retained; XivBlend never silently deletes them.
- Deleting the cache is recoverable: run setup again, then replay cards as needed.
- Do not distribute cache files or copy them into a shared export folder.

## Fidelity limitations

The body Action is still the primary PAP track. TMB-scheduled face clips are reconstructed, but other auxiliary skeletal layers—such as some ear, tail, part, or additive tracks—are not yet merged. Root motion, game transitions, environment interaction, and emote conditions are not reproduced.

The apple and glowsticks are intentionally lightweight Blender-native approximations. Timing—and glowstick color—comes from the emote bundle; the browser attaches them to known right/left hand-bone candidates on the exported rig. Their geometry and arbitrary AVFX behavior are not exact game rendering. Unsupported visual events appear as warnings rather than producing junk objects.

The browser loops previews for inspection even when the original emote is a one-shot. Common `c0101` body fallback uses named-bone retargeting, not FFXIV's complete retargeting system, so proportion-sensitive translation still needs validation across all player rigs.

## Validation

The current catalog audit parsed all 250 body-emote primary PAP timelines with zero parser failures. It observed 210 declared face libraries, 736 scheduled face events resolving to 437 exact facial clips on the validated `c0101/f0002` rig, 149 VFX events, and 55 prop events. Eat Apple and cheer-wave timing were checked directly against their live TMB/PAP data.

Schema-2 catalog/request handling, schema-1 bundle loading, timed NLA evaluation, Blender 5 Action-slot handling, apple/glowstick creation, complete cleanup, and registration pass headless tests in Blender 5.0 and 5.2. Broader live testing across every race, face rig, custom animation style, and future game update remains necessary.

## Vanilla verification and local-only boundary

Vanilla setup and decode requests are refused when Dalamud reports modified live SqPack files. That advisory cannot independently prove that TexTools never modified a live index; restore/reset TexTools index changes first. Penumbra does not need to be disabled for vanilla cards because canonical SqPack reads bypass it.

The explicit custom importer is a separate path: Penumbra remains the conflict and option resolver, and XivBlend accepts only the selected mod's active winning PAP files for the local player. No arbitrary folder scan is exposed to Blender.

No Square Enix icon, PAP, SKLB, generated GLB, bundle, or cache file is committed to this repository or shipped in the plugin package. FFXIV and mod data remain their respective owners' property. Keep extracted files local, do not redistribute paid mod assets, and follow the applicable FFXIV agreements.

The in-process Havok loading/sampling and bounded TMB reader are adapted from the MIT-licensed [Dalamud VFXEditor](https://github.com/0ceal0t/Dalamud-VFXEditor/tree/cd878d0e029d515acef723494ea4ffe5dbe19ade). See [NOTICE-XIVBLEND.md](../NOTICE-XIVBLEND.md) for the pinned revision and license notice.
