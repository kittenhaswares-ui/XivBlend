# XivBlend Prototype 0.0.17

This update fixes dark opaque patches in densely layered modded hair and fur and adds an optional shadowed Cycles portrait look.

## What changed

- Raises only Cycles' transparent-ray depth from 8 to 128. Ordinary light-path depth remains 8, so opaque scenes do not pay for extra diffuse, glossy, or transmission bounces.
- Applies the correction to new exports and automatically migrates the saved Render Studio preset when an existing `.blend` opens; **Beauty**, **Detail**, and **Render Portrait** also reassert it.
- Adds **Render Studio** → **Detail**: the same materials and 256-sample adaptive Cycles setup as Beauty, but with a tighter key, much less fill and ambient light, and deeper shadows for face, hair, normal-map, and clothing-fold detail.
- Restores the exact soft Beauty rig when switching back to Beauty, Preview, or Animate; repeated switching cannot accumulate light changes.
- Keeps source textures and alpha masks unchanged; there is no texture conversion or quality loss.
- Adds Blender 5.0 and 5.2 regression coverage for both configuration paths.

The failing Wheel tail contains up to 96 overlapping transparent fur cards along a tested camera ray. The previous limit stopped those rays early and rendered the remaining layers black; 128 safely clears the measured stack.

## Install and test

1. Update or reload XivBlend 0.0.17.
2. In **Animations**, press **Set Up / Update Animation Browser** and wait for setup to finish without red text.
3. Close and restart Blender so the running session loads browser 0.8.0.
4. Reopen the same character `.blend`; no character re-export is required.
5. In the XivBlend sidebar, select **Render Studio** → **Beauty** again for the soft look, or **Detail** for deeper shadows, and render.

Dense transparent hair or fur can take somewhat longer to render because Cycles now follows those rays until the actual cards are cleared. **Animate** and Eevee **Preview** are unchanged.
