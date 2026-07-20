# XivBlend Prototype 0.0.4

This release tightens live-character fidelity and makes the generated Blender scene presentation-ready by default.

## Install

Add the repository URL from the README to Dalamud's Custom Plugin Repositories, or download and extract `latest.zip` and add the exact `XivBlend.Plugin.dll` under Dev Plugin Locations.

Disable the original Meddle plugin before loading XivBlend. Blender 5.x, Penumbra, and Glamourer are recommended.

## Current scope

- Exports only the logged-in local player.
- Includes visible body, face, hair, equipment, weapons, live materials/textures, morphs, skin weights, and rig.
- Excludes the known non-rendering b0003 body proxy instead of placing an opaque shell over the character.
- Creates a packed portrait-oriented Blender scene with fitted camera, three-point lighting, and a removable studio backdrop.
- Gives the rig a thin stick display, removes empty vertex groups, and performs stronger mesh/weight/material validation.
- Creates a unique timestamped folder for every export and never overwrites or auto-deletes earlier exports.
- Embeds redacted provenance and a build report instead of the full private snapshot manifest.
- Does not include animations, physics, VFX, mounts, ornaments, or companions yet.

This release passed offline build/package, offline inspection of a previously captured live export, and Blender reopen validation. Live in-game coverage is still experimental. External manifests, Meddle snapshots, glTF/bin files, and caches remain private diagnostics; do not upload an export folder wholesale. Report failures with the red plugin error and a reviewed, redacted `xivblend-export-report.json`.
