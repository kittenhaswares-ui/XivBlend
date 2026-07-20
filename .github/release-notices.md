# XivBlend Prototype 0.0.2

This is the first public prototype of the self-only FFXIV character-to-Blender exporter.

## Install

Add the repository URL from the README to Dalamud's Custom Plugin Repositories, or download and extract `latest.zip` and add the exact `XivBlend.Plugin.dll` under Dev Plugin Locations.

Disable the original Meddle plugin before loading XivBlend. Blender 5.0+, Penumbra, and Glamourer are recommended.

## Current scope

- Exports only the logged-in local player.
- Includes visible body, face, hair, equipment, weapons, live materials/textures, morphs, skin weights, and rig.
- Creates a packed Blender scene through a headless Blender worker.
- Does not include animations, physics, VFX, mounts, ornaments, or companions yet.

This release passed offline build/package and Blender reopen validation. Live in-game coverage is still experimental; please report failures with the red plugin error and a redacted `xivblend-export-report.json`.
