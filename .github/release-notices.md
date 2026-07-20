# XivBlend Prototype 0.0.7

This patch fixes the first-click animation failure in version 0.0.6. XivBlend was reading PAP data correctly but comparing its little-endian header against a byte-reversed signature, so every valid vanilla animation was rejected as “not a PAP file” before Havok sampling began.

## What changed

- Corrects PAP signature validation for the game’s `pap ` header bytes.
- Keeps the on-demand design: only the selected vanilla emote is read and converted, and successful clips remain in the shared local cache.
- Does not require a new character export when the `.blend` was already created with version 0.0.6.

## Install and test

Update or reload XivBlend, keep FFXIV running, and click an emote in Blender again. Previously failed requests are harmless; the new click creates a fresh request. The first uncached click needs the game and plugin running, while later playback uses the cached clip.

The current library remains deliberately limited to 279 vanilla player emotes and facial expressions. Combat/job actions, weapons, VFX, sound, props, mounts, movement, NPC-only animations, modded PAP files, TMB orchestration, and auxiliary multi-clip layers remain out of scope.

The fix passes the .NET Debug and Release builds plus static PAP/SKLB header validation against live SqPack data. Broad in-game sampling across every race, face rig, and emote remains prototype coverage work.
