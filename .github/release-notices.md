# XivBlend Prototype 0.0.5

This release makes the generated Blender rig cleaner to inspect and adds a beginner-friendly, script-free pose control.

## Install

Add the repository URL from the README to Dalamud's Custom Plugin Repositories, or download and extract `latest.zip` and add the exact `XivBlend.Plugin.dll` under Dev Plugin Locations.

Disable the original Meddle plugin before loading XivBlend. Blender 5.x, Penumbra, and Glamourer are recommended.

## Current scope

- Exports only the logged-in local player.
- Includes visible body, face, hair, equipment, weapons, live materials/textures, morphs, skin weights, and rig.
- Excludes the known non-rendering b0003 body proxy instead of placing an opaque shell over the character.
- Creates a packed portrait-oriented Blender scene with fitted camera, three-point lighting, and a removable studio backdrop.
- Disables the glTF-imported Icosphere custom bone widgets that overrode Blender's `STICK` setting and caused the apparent blocky armature. New files open with the clean stick rig visible.
- Hides the viewport grid, coordinate axes, relationship lines, camera, and light helpers by default while keeping the camera and studio lights active for rendering.
- Adds a normal Blender Timeline slider: frame 0 is labeled `XIV A-POSE`, frame 100 is labeled `CAPTURED POSE`, and the file opens at frame 100. It uses ordinary linear keyframes, so it needs no add-on or trusted embedded script.
- Preserves the native glTF rest matrices, bind transforms, and bone axes. Primary `X` / Secondary `Y` is the FFXIV FBX round-trip convention only at the FBX boundary; the Blender rig is not remapped.
- Removes empty vertex groups and performs stronger mesh/weight/material validation.
- Creates a unique timestamped folder for every export and never overwrites or auto-deletes earlier exports.
- Embeds redacted provenance and a build report instead of the full private snapshot manifest.
- Does not include the FFXIV emote/action animation library, physics, VFX, mounts, ornaments, or companions yet.

Existing `.blend` files are not retroactively changed; export again with 0.0.5 for the new rig, viewport, and Timeline defaults.

Version 0.0.5 passed the release build, Blender reopen checks, and an offline end-to-end test using a previously captured local live-character export. Broader live in-game coverage is still pending. External manifests, Meddle snapshots, glTF/bin files, and caches remain private diagnostics; do not upload an export folder wholesale. Report failures with the red plugin error and a reviewed, redacted `xivblend-export-report.json`.
