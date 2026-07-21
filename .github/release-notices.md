# XivBlend Prototype 0.0.14

This update makes character exports easier to navigate, repairs the black static AVFX previews, and preserves the original attachment-bone basis needed to orient supported food/item props correctly in Blender.

## What changed

- Groups exported character meshes into readable category-first collections such as `Hair - <Mod Name>`, `Upper Body - <Mod Name>`, and `Legs - <Mod Name>`. Imported object and mesh datablock names remain unchanged.
- Records the original glTF basis for attachment bones and applies that correction before the game's ATCH transform. This fixes the Blender axis mismatch for supported item props, including the verified consumable path, instead of adding a pizza-specific rotation.
- Keeps TMB `C198` props on their directly encoded model/body/variant rather than guessing through the Item sheet; Pizza is authored as `w9901/b0033/v1`, while the inventory item's shared display model is a different triple.
- Gives compatible older XivBlend files a narrow fallback for known hand/throw sockets. Arbitrary rigs, unusual attachment behavior, and unverified item classes are not guessed.
- Changes embedded static AVFX previews to cache format v2 with a neutral unlit, double-sided material, preventing valid geometry/vertex color from appearing pitch black under Blender's default metallic interpretation.
- Continues to preserve the exact native AVFX source and timing/placement metadata. Full Apricot emitters, particles, trails, distortion, collision, animated curves, and game shaders are not implemented; static geometry remains inspection-only.
- Updates the plugin to 0.0.14, Blender animation browser to 0.6.0, character builder to 0.9.0, and animation bundles to schema 3. Catalog/request manifests remain schema 2; older bundles and static-preview-v1 cache entries rebuild on demand.

## Install and test

1. Update or reload XivBlend 0.0.14.
2. In **Animations**, press **Set Up / Update Animation Browser** to install browser 0.6.0.
3. Export the character again to receive category/mod collections and exact saved socket-basis metadata.
4. Click **Refresh Game Catalog** or simply click an emote and allow its old schema-2 bundle/static-preview-v1 asset to rebuild.

The plugin and Blender panel still ship without FFXIV assets. Exact props and AVFX sources are extracted locally only when a selected emote needs them, remain in the user's shared cache, and must not be redistributed. Existing XivBlend exports can use the known-socket fallback, but a fresh 0.0.14 export is required for the exact source-basis metadata and the new collection organization.
