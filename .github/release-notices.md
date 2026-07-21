# XivBlend Prototype 0.0.10

This focused lighting update gives pale skin, clothing folds, and character silhouettes more depth instead of illuminating the whole subject too evenly.

## What changed

- Reduces the warm key-light output by roughly 44% and makes its color more neutral.
- Uses a smaller softbox footprint for soft but more readable directional shadows.
- Reduces the broad fill by roughly 79%, allowing the key-side modelling to remain visible.
- Reduces the cool rim by roughly 54% so it separates the silhouette without overpowering materials.
- Slightly lowers exposure while retaining the existing 1440×1800, RGB16, 96-sample Eevee output.
- Updates builder validation and the strict scene audit to enforce the refined preset.

## Install and test

1. Update or reload XivBlend 0.0.10.
2. Export the character again; existing `.blend` files retain their saved 0.0.9 lights.
3. Render the captured pose or choose an emote pose and use **XivBlend** → **Render Studio** → **Fit Camera to Current Pose** first.

The character extraction, rig, materials, animation browser, cache, and packed-file behavior are unchanged by this patch.
