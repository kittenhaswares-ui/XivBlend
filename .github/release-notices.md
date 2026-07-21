# XivBlend Prototype 0.0.13

This update replaces the first fake emote props with exact local game assets, indexes native AVFX data without pretending Blender can already reproduce FFXIV's particle runtime, and fixes custom Penumbra animation mods whose player pose/loop files do not match a vanilla catalog entry.

## What changed

- Exports TMB-spawned props on demand from the local SqPack, including model, mapped materials, textures, colorset, hand selection, scale, lifetime, and verified food attachment data. Eat Apple now uses the real game prop.
- Preserves each native AVFX source, content hash, TMB timing/color/scale/placement, particle types, texture references, and embedded-geometry metadata in the shared local cache.
- Classifies effects that need FFXIV's Apricot runtime instead of substituting fake apples, glowsticks, or other Blender geometry. Static embedded AVFX meshes are inspection data only; full particle playback is not implemented yet.
- Uses Penumbra's physical mod-directory API and bounded standard mod manifests, fixing the reported virtual-path `DirectoryNotFoundException`.
- Discovers active standalone player pose/loop PAPs as **Custom** cards even when they do not replace a known vanilla emote.
- Updates the Blender animation browser to 0.5.0 and animation bundles to schema 2. Old schema-1 bundles rebuild automatically.

## Install and test

1. Update or reload XivBlend 0.0.13.
2. In **Animations**, press **Set Up / Update Animation Browser** to install browser 0.5.0.
3. Click **Refresh Game Catalog** or simply click an emote and allow its old bundle to rebuild.
4. For a custom mod, press **Refresh Penumbra Mods**, choose it, then press **Add Active Animation Overrides**. Active standalone pose/loop files should now appear under **Custom** in Blender.

The plugin and Blender panel still ship without FFXIV assets. Exact props and AVFX sources are extracted locally only when a selected emote needs them, remain in the user's shared cache, and must not be redistributed.
