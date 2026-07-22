# XivBlend Prototype 0.0.16

This maintenance update fixes the failed Blender-panel upgrade that left an older browser active, and repairs black food props without replacing their real FFXIV textures.

## What changed

- Defers old Render Studio cleanup until Blender has finished enabling the add-on. Blender 5.2 no longer rejects the browser with `_RestrictData` and rolls back to an older copy.
- Uses one transparent neutral texture when a temporary game prop has a linked but absent optional decal. Real diffuse, normal, mask, index, tile, and decal textures remain untouched.
- Installs animation browser 0.7.1, so the saved original hand-socket basis is actually used for supported consumable props instead of the old skyward orientation.
- Adds Blender 5.0 and 5.2 regression coverage for restricted add-on registration and Pizza/Apple runtime materials.

## Install and test

1. Update or reload XivBlend 0.0.16.
2. In **Animations**, press **Set Up / Update Animation Browser** and wait for setup to finish without red text.
3. Close and restart Blender so the running session loads browser 0.7.1.
4. Reopen the same character `.blend`; no character re-export is required.
5. Click Apple Eating or Pizza Eating again. An older schema-2 bundle is rebuilt automatically by the current plugin.

The plugin and Blender panel still ship without FFXIV assets. Exact props and AVFX sources are extracted locally only when a selected emote needs them, remain in the user's shared cache, and must not be redistributed.
