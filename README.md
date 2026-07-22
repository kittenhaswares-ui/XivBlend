# XivBlend

XivBlend is an experimental Dalamud plugin that turns **your own currently displayed FFXIV character** into a ready-to-use Blender file. It reads the character after Glamourer and Penumbra have applied the current appearance, then exports the visible body, face, hair, clothes, accessories, weapons, materials, textures, skin weights, morphs, and rig.

It has no target-player export button. It is deliberately limited to the character you are logged in as.

## Before you start

You need:

- Windows x64 and FFXIV running through XIVLauncher/Dalamud API 15.
- [Blender 5.x](https://www.blender.org/download/).
- Penumbra and Glamourer if you want to export a modded appearance.
- Enough free disk space. Every export keeps its working files and textures.

**Disable the original Meddle plugin before enabling XivBlend.** XivBlend contains the Meddle extraction foundation, so running both at once can make them hook the same game data.

## Install XivBlend

The easiest method is the plugin repository:

1. In FFXIV, open `/xlsettings`.
2. Go to **Experimental** → **Custom Plugin Repositories**.
3. Add this address:

   ```text
   https://raw.githubusercontent.com/kittenhaswares-ui/XivBlend/main/repo.json
   ```

4. Open `/xlplugins` and install **XivBlend Prototype**.

For a manual test build, download `latest.zip` from the [latest release](https://github.com/kittenhaswares-ui/XivBlend/releases/latest), extract it, and add the extracted `XivBlend.Plugin.dll` under **Dev Plugin Locations**. Keep all files from the zip together.

## Quick export your character

1. Finish the appearance in Glamourer and Penumbra.
2. Leave Group Pose.
3. Redraw the character in Penumbra and wait until everything is visible.
4. Run `/xivblend`.
5. Open **Export My Character**.
6. If needed, use **Browse...** to select Blender's `blender.exe`.
7. Press **Export My Character to Blender**.

Blender runs in the background, builds the scene, and saves the result. XivBlend then shows the finished path and an **Open Export Folder** button. Each click creates a **new timestamped folder** under:

```text
Documents\XivBlend Exports\XivBlend-<character>-<time>\
```

Nothing is overwritten. This is safer, but old exports can use a lot of space. Once you know a packed `.blend` works, you may manually delete export folders you no longer need.

## Use animations

Open XivBlend's **Animations** tab in FFXIV and press **Set Up / Update Animation Browser**. This creates the local emote catalog and installs the XivBlend panel in Blender. Restart Blender if it was already open.

In Blender:

1. Put the mouse over the 3D View and press `N`.
2. Open the **XivBlend** tab.
3. Open **Player Emotes** and click an emote icon.
4. Use **Stop / Restore Captured Pose** when you want the original exported pose back.

Keep FFXIV running with XivBlend enabled the first time you click an uncached emote. XivBlend extracts only that selected animation bundle. Later plays use the local cache. Supported emotes can include their body animation, timed facial animation, real game prop, and AVFX information on the same 30 fps timeline.

To add a Penumbra animation mod, use **Refresh Penumbra Mods**, choose the mod, and press **Add Active Animation Overrides**. XivBlend reads only the active winning player-animation files for your own character. It does not change the mod or its options.

Combat actions, weapon actions, mounts, sound, and NPC animations are not included. Blender can inspect cached AVFX data and some embedded static geometry, but it cannot yet reproduce FFXIV's full Apricot particle system.

## Make a clean picture

The exported file already contains a portrait camera, a studio sweep, lights, packed textures, and a stick-style armature. Open **XivBlend** → **Render Studio** in Blender's `N` sidebar:

- **Animate** uses Blender's fast Solid viewport. Use it while posing or playing animation.
- **Preview** uses Eevee with the real materials for quick visual checks.
- **Beauty** uses softly filled Cycles studio lighting, adaptive sampling, denoising, and enough transparent ray depth for dense layered hair and fur cards.
- **Detail** uses the same clean Cycles renderer with a tighter key light, much less fill, and deeper shadows to reveal face, hair, normal-map, and clothing-fold detail.

You can choose a charcoal, neutral gray, or transparent background; attractive AgX color or more neutral mod-accurate color; and Web PNG, 16-bit PNG, or EXR output. **Fit Camera to Current Pose** frames one pose. **Fit Camera to Whole Animation** leaves room for the full movement. **Render Portrait** renders the current frame.

The normal Timeline also contains an A-pose at frame 0 and the captured game pose at frame 100. Moving between them blends the pose without requiring the animation add-on.

## Common problems

- **Export does not start:** confirm Blender 5.x is selected and the original Meddle plugin is disabled.
- **The Blender panel is missing:** run **Set Up / Update Animation Browser** again, then restart Blender and press `N` in the 3D View.
- **Animation-browser setup is red:** the previous Blender panel is still active because the update did not install. Update XivBlend, run **Set Up / Update Animation Browser** again, and restart Blender before testing emotes. You can reopen the same `.blend`; click the emote again so XivBlend rebuilds any older cached bundle.
- **An emote does nothing:** keep FFXIV and XivBlend open, refresh the game catalog, and click again after extraction finishes.
- **Animation playback is slow:** use **Animate**. Eevee and especially Cycles calculate materials, lighting, and shadows and are intended for previews or final stills.
- **Dense hair or fur has black blocks in Beauty:** update XivBlend, reinstall the Blender panel, and restart Blender. Browser 0.8.0 migrates the saved render preset when the existing `.blend` reopens; no character re-export is needed.
- **Setup reports modified game files:** restore TexTools index changes. Normal Penumbra mods do not need to be disabled.
- **Disk use keeps growing:** repeated exports are separate by design; remove old timestamped folders manually.

## Privacy and sharing

The packed `.blend` has redacted provenance, but names in the rig, materials, or textures may still identify a character or mod. The other files in the export folder are more sensitive: manifests, Meddle snapshots, glTF files, and the cache may contain the character name, Glamourer state, absolute local mod paths, or extracted assets.

Do not upload the whole export folder. Check and redact diagnostic files before sharing a bug report. Do not redistribute FFXIV assets, paid mod files, or another creator's work without permission.

XivBlend is an unofficial prototype. Materials are close Blender translations, not a pixel-perfect copy of the FFXIV renderer, and game updates can temporarily break extraction.

Developers can read the [Code Audit](docs/CODE_AUDIT.md), [Architecture](docs/ARCHITECTURE.md), and [Animation Library](docs/ANIMATION_LIBRARY.md). Licensing and upstream attribution are in [LICENSE.txt](LICENSE.txt) and [NOTICE-XIVBLEND.md](NOTICE-XIVBLEND.md).
