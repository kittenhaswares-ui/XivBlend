# XivBlend Prototype 0.0.6

This release adds an optional, beginner-friendly Blender browser for vanilla player emotes and facial expressions without embedding the complete animation library in every character file.

## Install

Add the repository URL from the README to Dalamud's Custom Plugin Repositories, or download and extract `latest.zip` and add the exact `XivBlend.Plugin.dll` under Dev Plugin Locations.

Disable the original Meddle plugin before loading XivBlend. Blender 5.x is required for character export; Penumbra and Glamourer are recommended for a modded appearance.

## Animation browser

- Adds an **Animations** tab with one-click setup/update of the local catalog and Blender sidebar.
- Shows the game's own emote icons in Blender with search, category filters, paging, looping playback, and **Stop / Restore Captured Pose**.
- Strictly scopes the current validated catalog to 279 primary icon-click animations: 150 General, 100 Special, and 29 Expressions; commandless internal/event variants are excluded.
- Includes only vanilla player emotes and facial expressions from timeline slot zero.
- Excludes combat/job actions, weapon timelines, draw/sheathe, weapons, VFX, sound, props, mounts, movement, NPC-only animations, and every modded PAP.
- Reads canonical paths from the live SqPack and bypasses Penumbra. XivBlend refuses when Dalamud reports modification, but current Dalamud cannot independently prove that a live index was never changed by TexTools; restore TexTools index modifications first.
- Stores the catalog, locally converted icons, and requested clips in a versioned shared cache under `%LOCALAPPDATA%\XivBlend\AnimationLibrary`.
- Decodes only the icon clicked. Keep FFXIV running with XivBlend loaded on the first uncached click; later playback uses the cached clip.
- Treats preview Actions as runtime-only: Blender restores the exported captured pose and removes the preview Action before writing the `.blend`, then can resume it in the open session.
- Ships no FFXIV PAP, SKLB, icon, generated clip, or other game asset.

Only the primary PAP clip is sampled in this first version. TMB orchestration and auxiliary multi-clip layers are not merged, so some ear/tail/part/additive motion can be missing; a deterministic main-track fallback is used for known PAP/TMB naming mismatches but is not yet audited across the full catalog. Props and VFX remain intentionally out of scope.

## Character export

- Exports only the logged-in local player, including visible body, face, hair, equipment, weapons, live materials/textures, morphs, skin weights, and rig.
- Creates a packed portrait scene with a clean stick armature, fitted camera, three-point lighting, and removable studio backdrop.
- Keeps the script-free Timeline control: frame 0 is `XIV A-POSE`, frame 100 is `CAPTURED POSE`, and files open at frame 100.
- Embeds safe race and captured face-skeleton identifiers for the animation browser, while keeping private paths and full manifests external.
- Preserves native glTF rest matrices and bone axes. Primary `X` / Secondary `Y` remains an FBX-boundary convention only.
- Creates a unique timestamped folder for every export and never overwrites or auto-deletes earlier exports.

Existing `.blend` files are not retroactively changed. Export again with 0.0.6 to add the animation lookup metadata.

The 0.0.6 implementation passes the current .NET build and Blender-side metadata/add-on checks. Broad live first-click decoding across every race, face rig, and emote remains pending. External manifests, Meddle snapshots, glTF/bin files, caches, icons, and generated animation clips are private diagnostics/assets; do not upload an export or animation-cache folder wholesale. Report failures with the red plugin error and only reviewed, redacted text diagnostics.
