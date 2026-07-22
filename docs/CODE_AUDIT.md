# XivBlend code audit

This page answers four practical questions: what came from Meddle, what belongs to XivBlend, what was unused, and why some old-looking code remains. It describes the cleanup performed before this document was written; it is not a promise that every future file count will stay identical.

## The short answer

XivBlend is a real product layer built on Meddle's extraction foundation. Meddle already knew how to read a live FFXIV draw object and turn models, skeletons, materials, colorsets, textures, and deformation data into glTF. XivBlend adds the self-only one-button workflow, Penumbra/Glamourer snapshot handling, readable mod collections, the Blender builder, the animation library, synchronized faces and props, AVFX inspection, custom PAP imports, and the portrait Render Studio.

The cleanup removed Meddle features that XivBlend never presents or calls. It did not rewrite working low-level extraction simply to make filenames look newer.

## Starting point: measured inheritance

The initial audit examined 156 C# source/project files in the plugin and utility tree, excluding generated `bin`/`obj` output and separately pinned third-party assets. Comparison with the pinned Meddle source found:

| Initial category | Files | Meaning |
| --- | ---: | --- |
| Byte-identical Meddle files | 121 | Copied from the pinned upstream version without source changes |
| Inherited files modified for XivBlend | 15 | Meddle files adapted for the new export path or plugin host |
| New XivBlend files | 20 | Product-specific C# services, UI, integration, and data models |
| **Total examined** | **156** | Baseline before this cleanup |

These numbers describe origin, not value. A byte-identical parser can be essential, while a large custom debug window can be completely unreachable. “Inherited” does not mean “dead,” and “new” does not automatically mean “good.”

## Are blank lines or old namespaces waste?

No. Blank lines make code easier for people to read. They do not become executable instructions and have no meaningful effect on plugin speed, Blender speed, memory use, or the release package.

The `Meddle.Plugin` namespace is also not a second plugin hiding inside XivBlend. A namespace is an organizational name used by C#; it does not load features by itself. The built assembly is `XivBlend.Plugin`. Keeping the inherited namespace makes upstream comparison and license history clearer, avoids a huge rename-only diff, and reduces the chance of breaking serialization or generated interop.

Files were therefore judged by references and runtime reachability, not by blank-line count, filename, or namespace.

## What this cleanup removed

### First unreachable island: 7,215 lines

The first pass removed 7,215 lines from an unreachable legacy island. This included old Meddle windows and panels for debugging, live-character inspection, material testing, terrain/world layout viewing, the previous animation/export interface, texture previews, and UI-only helper code. Only XivBlend's **Export My Character** and **Animations** tabs are registered now.

The deletion was safe because these screens were not registered by the XivBlend window manager and no active product service depended on them. A fully commented destructor-hook file was removed as well; comments do not provide runtime behavior.

### Character-capture narrowing: roughly 2,800 lines

The second pass removed roughly 2,800 more lines by tracing the real one-button path from `LocalPlayer` to `CharacterComposer`. World, housing, terrain, light, decal, and shared-group instance parsing were not on that path. `LayoutService`, `InstanceComposer`, their world data structures, and the old LGB/TERA readers could therefore leave together.

`ResolverService` was reduced to its actual job: capture the logged-in character's draw object and its visible weapons. `StainProvider` was reduced to the dye-sheet lookup required by material parsing. Active character snapshot types were moved out of the deleted world-layout model, and the tiny export progress helper received a name that describes what it does.

### Configuration and service wiring

A final pass removed legacy debug, secret, player-name, layout, terrain, updater, and preview settings together with obsolete compatibility types. The remaining configuration contains only plugin-window behavior, export/Blender options, the active mesh-export switches, and the RSF cache. Its compact migration normalizes old UI-hide values, the former temporary export path, missing settings objects, and the version in one save; removed JSON members remain safely ignorable.

Service wiring now explicitly registers only the active character-export, animation, provenance, Blender-installer, deformation, dye, and UI services. The generic `HookManager` was removed; `PbdHooks` now owns and disposes its single native hook directly. Blender executable discovery and companion versions have one shared C# home. Three final unreferenced leaves also went away: an outdoor-layout memory overlay, an unused shader-key overlay, and old generic enum drawing helpers. Redundant injected Dalamud services and the superseded export-folder notification helper were removed as well. No line total is attached to this final pass because the important result is an accurate service graph, not a deletion score.

## What remains intentionally

The remaining inherited foundation is needed by a currently reachable XivBlend feature:

- `CharacterComposer`, `ComposerCache`, and `MaterialComposer` build the character and prop glTF data.
- `ResolverService` and `ParseMaterialUtil` read the final live character, material, skeleton, and visible-weapon state.
- `PbdHooks`, `SkeletonUtils`, and the focused `StainProvider` provide deformation, skeleton, and dye information.
- `Meddle.Utils` contains SqPack readers and model, material, texture, and glTF conversion used by the export pipeline.
- The pinned MeddleTools runtime maps exported FFXIV shader data inside Blender.
- `RsfWatcher` and its small saved cache remain because skeleton resource data still participates in deformation export.
- License files, notices, and original namespace history remain on purpose. Attribution is not dead code.

The XivBlend-owned layer remains centered on quick export, part provenance, animation catalog/request serving, PAP/TMB/prop/AVFX extraction, custom Penumbra animations, the Blender scene builder, and the Blender sidebar. A file-by-file product map is available in [Architecture](ARCHITECTURE.md).

## How deletion safety was checked

The cleanup used a conservative loop:

1. Start from the only registered UI tabs and the self-only export/animation services.
2. Trace constructor dependencies, direct calls, and serialized compatibility types.
3. Remove a whole unreachable feature island, not random helper methods one at a time.
4. Search for surviving references and stale UI wording.
5. Build the release configuration and run the utility and Blender-facing checks.

This matters because reflection, dependency injection, serialization, Blender metadata, and native hooks are not always obvious from a single text search. A suspicious name alone is not enough evidence to delete a file.

## Next safe refactor hotspots

The codebase is substantially cleaner, but several improvements can still be made without changing the user workflow:

- Split the large `AnimationLibraryService` into catalog building, request validation, cache management, and bundle assembly behind small interfaces.
- Split the Blender animation-browser Python file into reviewed modules for catalog I/O, animation playback, effects, camera fitting, and Render Studio while keeping one simple installed add-on.
- Add a build-time check that the centralized C# companion versions match the embedded Python builder/browser constants, and that schema numbers agree at every producer/consumer boundary.
- Audit the bundled MeddleTools package by import reachability. Remove a module only after proving that character building, prop material mapping, and installation do not use it.
- Add focused dependency tests that fail if a removed world/layout service is registered again or if the self-only export path gains an arbitrary target input.
- Consider moving the stable extraction foundation into its own project boundary later. Avoid a mass namespace rename until APIs and serialized formats are deliberately versioned.

The best next cleanup is modularization with tests, not another blind line-count reduction.
