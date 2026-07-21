# XivBlend Prototype 0.0.9

This release gives new character exports a cleaner, grounded portrait studio and adds simple camera controls for still poses and complete animation clips.

## What changed

- Fits the default 70 mm portrait camera to the real captured pose, avoiding the excess headroom caused by sampling artificial in-between slider poses.
- Anchors the seamless studio sweep to the FFXIV rig's world ground plane so a low tail, robe, or sheathed weapon cannot drag it down and make the feet float.
- Replaces the coarse banded background with a dense, smooth navy gradient sweep.
- Uses a warm rectangular key, broad cool fill, and restrained cool rim for softer skin, fabric, and metal shading.
- Raises the default output to 1440×1800, 16-bit RGB PNG, and 96-sample Eevee while keeping ray tracing disabled for practical render speed.
- Updates the companion Blender panel to version 0.2.0 with **Fit Camera to Current Pose**, **Fit Camera to Whole Animation**, and **Render Portrait** controls.
- Keeps camera fitting non-destructive: the lens, Action, current frame, subframe, and playback state are preserved.

## Install and test

1. Update or reload XivBlend 0.0.9.
2. In XivBlend's **Animations** tab, press **Reinstall Blender Panel** (or **Set Up / Update Animation Browser**) and wait for success.
3. Restart any Blender window that was already open.
4. Export the character again to receive the upgraded studio, then open the new `.blend`.
5. In the 3D View press `N`, open **XivBlend** → **Render Studio**, and use the current-pose or whole-animation camera fit before rendering.

The new camera buttons also work on compatible older XivBlend files after the panel is reinstalled, but their saved lights and backdrop are not changed. Cached emote clips are reused; no animation re-extraction is required.

The complete builder was validated against the current c0801 character export: 52 visible meshes, a 538-bone rig, all packed textures, the captured pose, privacy checks, studio checks, and camera framing passed. A full preview render completed in Eevee without enabling ray tracing. Combat/job actions, weapons, VFX, sound, props, mounts, movement, NPC-only animations, modded PAP files, TMB orchestration, and auxiliary multi-clip layers remain out of scope.
