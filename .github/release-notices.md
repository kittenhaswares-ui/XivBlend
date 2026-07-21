# XivBlend Prototype 0.0.8

This patch fixes the Blender-side reason version 0.0.7 could successfully create an animation clip while the character remained motionless.

## What changed

- Explicitly binds the imported glTF Action slot to the exported character armature in Blender 4.4 and newer.
- Keeps compatibility with legacy Actions on Blender versions without Action slots.
- Preserves safe preview cleanup and restores the original captured-pose Action and slot when playback stops or the file is saved.
- Updates the companion Blender animation browser to version 0.1.1.

Blender 5 stores animation channels in layered Action slots. The generated clip had real changing transforms, but copying its Action from the temporary import rig to the differently named character rig left `action_slot` empty. The timeline therefore played an Action connected to no object. XivBlend now carries the imported slot across explicitly.

## Install and test

1. Update or reload XivBlend 0.0.8.
2. In XivBlend's **Animations** tab, press **Reinstall Blender Panel** (or **Set Up / Update Animation Browser**) and wait for success.
3. Restart any Blender window that was already open, reopen the existing character `.blend`, and click an emote.

No character re-export or clip re-extraction is required. Clips already cached by 0.0.7 are reused immediately.

The fix was reproduced against the current c0801 character export and a live cached 117-frame emote: 96 shared rig bones evaluate across the Action, and **Stop / Restore Captured Pose** restores the original Action, slot, and frame 100. Combat/job actions, weapons, VFX, sound, props, mounts, movement, NPC-only animations, modded PAP files, TMB orchestration, and auxiliary multi-clip layers remain out of scope.
