"""XivBlend's lightweight, on-demand animation browser for Blender.

The add-on intentionally contains no FFXIV assets.  It reads a local catalog
created by the XivBlend Dalamud plugin and asks that plugin to build a missing
clip.  Imported Actions are runtime-only: save handlers restore the captured
pose and remove those Actions before Blender writes a .blend file.
"""

bl_info = {
    "name": "XivBlend Animation Browser",
    "author": "XivBlend contributors",
    "version": (0, 1, 1),
    "blender": (4, 2, 0),
    "location": "3D View > Sidebar > XivBlend",
    "description": "Browse and play locally extracted FFXIV player emotes",
    "category": "Animation",
}

import json
import math
import os
from pathlib import Path
import time
import uuid

import bpy
from bpy.app.handlers import persistent
from bpy.props import IntProperty, StringProperty
from bpy.types import Menu, Operator, Panel

try:
    import bpy.utils.previews as _previews
except Exception:  # pragma: no cover - unavailable only in unusual builds
    _previews = None


ADDON_ID = __package__ or __name__
CATALOG_FOLDER = Path("XivBlend") / "AnimationLibrary"
CURRENT_FILENAME = "current.json"
PAGE_SIZE = 12
POLL_SECONDS = 0.75
REQUEST_TIMEOUT_SECONDS = 300.0
TRANSIENT_PROPERTY = "xivblend_runtime_animation"
SOURCE_CLIP_PROPERTY = "xivblend_source_clip"
CAPTURED_ACTION_PREFIX = "XivBlend | A-Pose to Captured Pose"
CAPTURED_MARKER = "CAPTURED POSE"

_catalog = None
_catalog_signature = None
_preview_collection = None
_preview_paths = {}
_pending_requests = {}
_captured_actions = {}
_scene_settings = {}
_runtime_sessions = {}
_save_sessions = []
_status = "Ready"


class CatalogError(RuntimeError):
    pass


class ClipError(RuntimeError):
    pass


def _normal_key(value):
    return "".join(character.lower() for character in str(value) if character.isalnum())


def _field(mapping, *names, default=None):
    """Read PascalCase/camelCase (and harmless underscore) JSON variants."""
    if not isinstance(mapping, dict):
        return default
    normalized = {_normal_key(key): value for key, value in mapping.items()}
    for name in names:
        key = _normal_key(name)
        if key in normalized:
            return normalized[key]
    return default


def _as_list(value):
    return value if isinstance(value, list) else []


def _id_text(value):
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return str(value if value is not None else "")


def _read_json(path):
    try:
        with path.open("r", encoding="utf-8-sig") as stream:
            document = json.load(stream)
    except FileNotFoundError as error:
        raise CatalogError(f"File not found: {path}") from error
    except json.JSONDecodeError as error:
        raise CatalogError(f"Invalid JSON in {path.name}: {error.msg}") from error
    except OSError as error:
        raise CatalogError(f"Could not read {path}: {error}") from error
    return document


def _local_library_root():
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise CatalogError("LOCALAPPDATA is unavailable; XivBlend's animation library cannot be located")
    return (Path(local_app_data) / CATALOG_FOLDER).resolve()


def _inside(path, root):
    try:
        return os.path.commonpath((str(path.resolve()), str(root.resolve()))) == str(root.resolve())
    except (OSError, ValueError):
        return False


def _resolve_child(root, relative_value, label):
    value = str(relative_value or "").strip()
    if not value:
        raise CatalogError(f"The catalog does not specify {label}")
    candidate = Path(value)
    if candidate.is_absolute():
        resolved = candidate.resolve()
    else:
        resolved = (root / candidate).resolve()
    if not _inside(resolved, root):
        raise CatalogError(f"Unsafe {label}: the path leaves {root}")
    return resolved


def _catalog_paths():
    default_root = _local_library_root()
    current_path = default_root / CURRENT_FILENAME
    current = _read_json(current_path)
    if not isinstance(current, dict):
        raise CatalogError(f"{current_path.name} must contain a JSON object")

    root_value = _field(current, "LibraryRoot")
    library_root = default_root
    if root_value:
        requested_root = Path(str(root_value))
        if not requested_root.is_absolute():
            requested_root = default_root / requested_root
        requested_root = requested_root.resolve()
        # current.json is the trust anchor.  It may narrow the root, never redirect
        # extraction and request files outside XivBlend/AnimationLibrary.
        if not _inside(requested_root, default_root):
            raise CatalogError("current.json contains a LibraryRoot outside XivBlend/AnimationLibrary")
        library_root = requested_root

    catalog_value = _field(current, "CatalogPath", "CatalogRelativePath")
    if catalog_value:
        catalog_path = _resolve_child(library_root, catalog_value, "CatalogPath")
    elif _field(current, "Entries", "Emotes") is not None:
        catalog_path = current_path
    else:
        raise CatalogError(f"{current_path.name} does not specify CatalogPath")
    return default_root, library_root, current_path, catalog_path, current


def _file_stamp(path):
    try:
        stat = path.stat()
        return (str(path), stat.st_mtime_ns, stat.st_size)
    except OSError:
        return (str(path), None, None)


def _close_previews():
    global _preview_collection, _preview_paths
    if _preview_collection is not None and _previews is not None:
        try:
            _previews.remove(_preview_collection)
        except Exception:
            pass
    _preview_collection = None
    _preview_paths = {}


def _ensure_previews():
    global _preview_collection
    if _preview_collection is None and _previews is not None:
        _preview_collection = _previews.new()
    return _preview_collection


def _load_catalog(force=False):
    global _catalog, _catalog_signature
    default_root, library_root, current_path, catalog_path, current = _catalog_paths()
    signature = (_file_stamp(current_path), _file_stamp(catalog_path))
    if not force and _catalog is not None and signature == _catalog_signature:
        return _catalog

    document = current if catalog_path == current_path else _read_json(catalog_path)
    if not isinstance(document, dict):
        raise CatalogError(f"{catalog_path.name} must contain a JSON object")
    entries = _field(document, "Entries", "Emotes", "Animations")
    if not isinstance(entries, list):
        raise CatalogError(f"{catalog_path.name} does not contain an Entries array")

    usable_entries = []
    for raw_entry in entries:
        if not isinstance(raw_entry, dict):
            continue
        emote_id = _field(raw_entry, "EmoteId", "Id")
        name = str(_field(raw_entry, "Name", default="") or "").strip()
        variants = [item for item in _as_list(_field(raw_entry, "Variants")) if isinstance(item, dict)]
        if emote_id is None or not name or not variants:
            continue
        usable_entries.append(raw_entry)

    usable_entries.sort(key=lambda item: (
        str(_field(item, "Category", default="Other") or "Other").casefold(),
        str(_field(item, "Name", default="")).casefold(),
        _id_text(_field(item, "EmoteId", "Id")),
    ))
    game_version = str(
        _field(document, "GameVersion", default=_field(current, "GameVersion", default="")) or ""
    ).strip()
    _catalog = {
        "document": document,
        "entries": usable_entries,
        "game_version": game_version,
        "catalog_path": catalog_path,
        "catalog_parent": catalog_path.parent.resolve(),
        "library_root": library_root,
        "default_root": default_root,
        "current_path": current_path,
    }
    _catalog_signature = signature
    _close_previews()
    return _catalog


def _catalog_or_error(force=False):
    try:
        return _load_catalog(force=force), None
    except Exception as error:
        return None, str(error)


def _entry_id(entry):
    return _field(entry, "EmoteId", "Id")


def _entry_name(entry):
    return str(_field(entry, "Name", default="Unnamed emote") or "Unnamed emote")


def _entry_command(entry):
    return str(_field(entry, "Command", default="") or "")


def _entry_category(entry):
    return str(_field(entry, "Category", default="Other") or "Other")


def _variant_id(variant):
    return _field(variant, "VariantId", "Id")


def _find_entry(catalog, emote_id):
    wanted = _id_text(emote_id)
    return next((entry for entry in catalog["entries"] if _id_text(_entry_id(entry)) == wanted), None)


def _find_variant(entry, variant_id=""):
    variants = _as_list(_field(entry, "Variants"))
    requested = variant_id or _field(entry, "DefaultVariantId", default="")
    if requested:
        wanted = _id_text(requested)
        match = next((variant for variant in variants if _id_text(_variant_id(variant)) == wanted), None)
        if match is not None:
            return match
    return next((variant for variant in variants if bool(_field(variant, "IsDefault", default=False))), variants[0] if variants else None)


def _id_property(owner, *keys):
    if owner is None:
        return None
    for key in keys:
        try:
            value = owner.get(key)
            if value is not None and str(value).strip():
                return value
        except Exception:
            pass
        try:
            value = getattr(owner, key)
            if value is not None and str(value).strip():
                return value
        except Exception:
            pass
    return None


def _armature_from_object(obj):
    if obj is None:
        return None
    if obj.type == "ARMATURE":
        return obj
    if obj.type == "MESH":
        if obj.parent is not None and obj.parent.type == "ARMATURE":
            return obj.parent
        for modifier in obj.modifiers:
            if modifier.type == "ARMATURE" and modifier.object is not None:
                return modifier.object
    return None


def _target_armature(context, kind="Body"):
    active = _armature_from_object(getattr(context, "active_object", None))
    armatures = sorted(
        (obj for obj in bpy.data.objects if obj.type == "ARMATURE"),
        key=lambda obj: obj.name.casefold(),
    )
    if not armatures:
        return None
    keys = ("xivblend_face_skeleton", "XivBlendFaceSkeleton") if str(kind).casefold() == "face" else (
        "xivblend_race_code", "XivBlendRaceCode"
    )
    marked = [
        obj for obj in armatures
        if _id_property(obj, *keys) is not None or _id_property(obj.data, *keys) is not None
    ]
    if active is not None and active in marked:
        return active
    if marked:
        return marked[0]
    return active if active is not None else armatures[0]


def _rig_identity(context, target, require_face=True):
    owners = (target, getattr(target, "data", None), getattr(context, "scene", None))
    race_value = next((value for owner in owners if (value := _id_property(
        owner, "xivblend_race_code", "XivBlendRaceCode", "raceCode", "RaceCode"
    )) is not None), None)
    face_value = next((value for owner in owners if (value := _id_property(
        owner, "xivblend_face_skeleton", "XivBlendFaceSkeleton", "faceSkeleton", "FaceSkeleton"
    )) is not None), None)

    if race_value is None:
        raise ClipError(
            f"Armature '{target.name}' has no xivblend_race_code. Re-export the character with a compatible XivBlend version."
        )
    race_text = str(race_value).strip().lower()
    if race_text.startswith("c"):
        race_digits = race_text[1:]
    else:
        try:
            race_digits = str(int(float(race_text)))
        except ValueError as error:
            raise ClipError(f"Invalid xivblend_race_code on '{target.name}': {race_value}") from error
    if not race_digits.isdigit():
        raise ClipError(f"Invalid xivblend_race_code on '{target.name}': {race_value}")
    race = f"c{int(race_digits):04d}"

    if face_value is None:
        if require_face:
            raise ClipError(
                f"Armature '{target.name}' has no xivblend_face_skeleton. Re-export the character with a compatible XivBlend version."
            )
        return race, ""
    face = str(face_value).strip().lower()
    if face.isdigit():
        face = f"f{int(face):04d}"
    if not (len(face) == 5 and face.startswith("f") and face[1:].isdigit()):
        raise ClipError(f"Invalid xivblend_face_skeleton on '{target.name}': {face_value}")
    return race, face


def _format_clip_path(catalog, entry, variant, race, face):
    template = _field(variant, "CacheRelativePathTemplate", "CacheRelativePath")
    if not template:
        raise ClipError(f"'{_entry_name(entry)}' has no CacheRelativePathTemplate")
    values = {
        "race": race,
        "Race": race,
        "raceCode": race,
        "RaceCode": race,
        "face": face,
        "Face": face,
        "faceSkeleton": face,
        "FaceSkeleton": face,
        "emoteId": _entry_id(entry),
        "EmoteId": _entry_id(entry),
        "variantId": _variant_id(variant),
        "VariantId": _variant_id(variant),
    }
    try:
        relative = str(template).format_map(values)
    except (KeyError, ValueError) as error:
        raise ClipError(f"Invalid clip path template for '{_entry_name(entry)}': {error}") from error
    candidate = _resolve_child(catalog["catalog_parent"], relative, "CacheRelativePathTemplate")
    if candidate.suffix.casefold() not in {".glb", ".gltf"}:
        raise ClipError(f"Animation clip must be a .glb or .gltf file: {candidate.name}")
    return candidate


def _icon_value(catalog, entry):
    relative = _field(entry, "IconRelativePath")
    if not relative:
        return 0
    try:
        path = _resolve_child(catalog["catalog_parent"], relative, "IconRelativePath")
    except CatalogError:
        return 0
    if not path.is_file():
        return 0
    previews = _ensure_previews()
    if previews is None:
        return 0
    key = f"emote:{_id_text(_entry_id(entry))}"
    path_text = str(path)
    try:
        if _preview_paths.get(key) != path_text:
            if key in previews:
                previews.pop(key)
            previews.load(key, path_text, "IMAGE")
            _preview_paths[key] = path_text
        return previews[key].icon_id
    except Exception:
        return 0


def _filtered_entries(scene, catalog):
    search = str(getattr(scene, "xivblend_animation_search", "") or "").strip().casefold()
    category = str(getattr(scene, "xivblend_animation_category", "") or "")
    result = []
    for entry in catalog["entries"]:
        if category and category != "__ALL__" and _entry_category(entry) != category:
            continue
        searchable = " ".join((_entry_name(entry), _entry_command(entry), _entry_category(entry))).casefold()
        if search and search not in searchable:
            continue
        result.append(entry)
    return result


def _set_status(message):
    global _status
    _status = str(message)
    for window_manager in [getattr(bpy.context, "window_manager", None)]:
        if window_manager is not None:
            try:
                window_manager.xivblend_animation_status = _status
            except Exception:
                pass
    _redraw()


def _redraw():
    window_manager = getattr(bpy.context, "window_manager", None)
    if window_manager is None:
        return
    for window in window_manager.windows:
        screen = window.screen
        if screen is None:
            continue
        for area in screen.areas:
            if area.type == "VIEW_3D":
                area.tag_redraw()


def _stop_playback():
    window_manager = getattr(bpy.context, "window_manager", None)
    if window_manager is None:
        return
    for window in window_manager.windows:
        screen = window.screen
        if screen is None or not getattr(screen, "is_animation_playing", False):
            continue
        try:
            with bpy.context.temp_override(window=window, screen=screen):
                bpy.ops.screen.animation_play()
        except Exception:
            pass


def _start_playback():
    window_manager = getattr(bpy.context, "window_manager", None)
    if window_manager is None:
        return False
    for window in window_manager.windows:
        screen = window.screen
        if screen is None:
            continue
        if getattr(screen, "is_animation_playing", False):
            return True
        area = next((item for item in screen.areas if item.type == "VIEW_3D"), None)
        region = next((item for item in area.regions if item.type == "WINDOW"), None) if area else None
        try:
            override = {"window": window, "screen": screen}
            if area is not None:
                override["area"] = area
            if region is not None:
                override["region"] = region
            with bpy.context.temp_override(**override):
                bpy.ops.screen.animation_play()
            return True
        except Exception:
            continue
    return False


def _captured_action_for(target):
    current = target.animation_data.action if target.animation_data is not None else None
    if (
        current is not None
        and current.name.startswith(CAPTURED_ACTION_PREFIX)
        and not bool(current.get(TRANSIENT_PROPERTY, False))
    ):
        return current
    saved = _captured_actions.get(target.name)
    if saved is not None:
        try:
            if saved.name in bpy.data.actions and not bool(saved.get(TRANSIENT_PROPERTY, False)):
                return saved
        except ReferenceError:
            pass
    candidates = [
        action for action in bpy.data.actions
        if action.name.startswith(CAPTURED_ACTION_PREFIX) and not bool(action.get(TRANSIENT_PROPERTY, False))
    ]
    return sorted(candidates, key=lambda action: action.name.casefold())[0] if candidates else None


def _remove_action(action):
    if action is None:
        return
    try:
        if action.name in bpy.data.actions and action.users == 0:
            bpy.data.actions.remove(action)
    except (ReferenceError, RuntimeError):
        pass


def _restore_target(target, restore_scene=True):
    if target is None or target.type != "ARMATURE":
        return
    animation_data = target.animation_data
    transient = animation_data.action if animation_data is not None else None
    if transient is not None and not bool(transient.get(TRANSIENT_PROPERTY, False)):
        transient = None
    captured = _captured_action_for(target)
    if animation_data is None and captured is not None:
        animation_data = target.animation_data_create()
    if animation_data is not None:
        animation_data.action = captured
    if transient is not None:
        _remove_action(transient)
    _runtime_sessions.pop(target.name, None)

    if restore_scene:
        scene = bpy.context.scene
        settings = _scene_settings.get(target.name)
        if settings is not None:
            scene.frame_start, scene.frame_end, fps, fps_base = settings
            scene.render.fps = fps
            scene.render.fps_base = fps_base
        marker = scene.timeline_markers.get(CAPTURED_MARKER)
        scene.frame_set(marker.frame if marker is not None else 100)


def _purge_transient_actions(restore=True):
    for obj in list(bpy.data.objects):
        if obj.type != "ARMATURE" or obj.animation_data is None:
            continue
        action = obj.animation_data.action
        if action is not None and bool(action.get(TRANSIENT_PROPERTY, False)):
            if restore:
                _restore_target(obj, restore_scene=False)
            else:
                obj.animation_data.action = None
                _remove_action(action)
    for action in list(bpy.data.actions):
        if bool(action.get(TRANSIENT_PROPERTY, False)) and action.users == 0:
            _remove_action(action)


def _data_snapshot():
    names = (
        "objects", "actions", "meshes", "armatures", "materials", "images",
        "textures", "collections", "cameras", "lights",
    )
    return {name: set(getattr(bpy.data, name)) for name in names if hasattr(bpy.data, name)}


def _cleanup_import(snapshot, keep_action=None):
    new_objects = [obj for obj in bpy.data.objects if obj not in snapshot.get("objects", set())]
    for obj in new_objects:
        try:
            bpy.data.objects.remove(obj, do_unlink=True)
        except (ReferenceError, RuntimeError):
            pass

    # Remove only zero-user data created by this import.  Existing user data is
    # never touched, and the copied runtime Action is explicitly retained.
    for name, old_values in snapshot.items():
        if name == "objects":
            continue
        collection = getattr(bpy.data, name, None)
        if collection is None:
            continue
        for data_block in list(collection):
            if data_block in old_values or data_block == keep_action:
                continue
            try:
                if data_block.users == 0:
                    collection.remove(data_block)
            except (AttributeError, ReferenceError, RuntimeError):
                pass


def _set_object_mode(context):
    active = getattr(context, "active_object", None)
    if active is not None and active.mode != "OBJECT":
        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except RuntimeError as error:
            raise ClipError("Switch to Object Mode before playing an animation") from error


def _bind_action_slot(animation_data, action, source_slot_identifier):
    """Bind a layered Action's copied slot to the target armature.

    Blender 4.4+ Actions store channels in slots.  Assigning a copied glTF
    Action to a differently named armature can leave ``action_slot`` empty,
    which makes a healthy Action evaluate as a motionless pose.  Preserve the
    imported armature's slot explicitly; older legacy Actions need no slot.
    """
    if not hasattr(animation_data, "action_slot"):
        return
    slots = list(getattr(action, "slots", ()))
    if not slots:
        return
    selected = next(
        (
            slot for slot in slots
            if slot.identifier == source_slot_identifier
            and getattr(slot, "target_id_type", "OBJECT") == "OBJECT"
        ),
        None,
    )
    compatible = [
        slot for slot in slots
        if getattr(slot, "target_id_type", "OBJECT") == "OBJECT"
    ]
    if selected is None and len(compatible) == 1:
        selected = compatible[0]
    if selected is None:
        raise ClipError(
            f"Animation Action '{action.name}' has no unambiguous armature slot"
        )
    try:
        animation_data.action_slot = selected
    except Exception as error:
        raise ClipError(
            f"Animation Action '{action.name}' could not bind to its armature slot: {error}"
        ) from error
    if animation_data.action_slot is None:
        raise ClipError(
            f"Animation Action '{action.name}' did not bind to its armature slot"
        )


def _import_clip(context, target, clip_path, entry, variant, auto_play=True):
    if not clip_path.is_file():
        raise ClipError(f"Animation clip is not ready: {clip_path.name}")
    scene = context.scene
    previous_scene_settings = (
        int(scene.frame_start), int(scene.frame_end),
        int(scene.render.fps), float(scene.render.fps_base),
    )
    # glTF stores key times in seconds, but Blender's importer converts those
    # seconds to Action frame numbers using the scene FPS at import time. Force
    # XIV's 30 fps before importing or a default 24 fps scene plays 25% fast.
    scene.render.fps = 30
    scene.render.fps_base = 1.0
    snapshot = _data_snapshot()
    original_active = getattr(context.view_layer.objects, "active", None)
    original_selected = list(getattr(context, "selected_objects", []))
    copied_action = None
    source_slot_identifier = None
    try:
        _set_object_mode(context)
        result = bpy.ops.import_scene.gltf(filepath=str(clip_path), disable_bone_shape=True)
        if "FINISHED" not in result:
            raise ClipError(f"Blender could not import {clip_path.name}")
        imported_objects = [obj for obj in bpy.data.objects if obj not in snapshot.get("objects", set())]
        source_action = None
        for imported in imported_objects:
            if imported.type != "ARMATURE" or imported.animation_data is None:
                continue
            if imported.animation_data.action is not None:
                source_action = imported.animation_data.action
                source_slot = getattr(imported.animation_data, "action_slot", None)
                if source_slot is not None:
                    source_slot_identifier = source_slot.identifier
                break
        if source_action is None:
            new_actions = [action for action in bpy.data.actions if action not in snapshot.get("actions", set())]
            source_action = new_actions[0] if new_actions else None
        if source_action is None:
            raise ClipError(f"{clip_path.name} contains no animation Action")

        copied_action = source_action.copy()
        copied_action.name = f"XivBlend Runtime | {_entry_name(entry)}"
        copied_action.use_fake_user = False
        copied_action[TRANSIENT_PROPERTY] = True
        copied_action[SOURCE_CLIP_PROPERTY] = str(clip_path)
        copied_action["xivblend_emote_id"] = _id_text(_entry_id(entry))
        copied_action["xivblend_variant_id"] = _id_text(_variant_id(variant))
    except Exception:
        _cleanup_import(snapshot, keep_action=copied_action)
        if copied_action is not None:
            _remove_action(copied_action)
        scene.render.fps = previous_scene_settings[2]
        scene.render.fps_base = previous_scene_settings[3]
        raise
    else:
        _cleanup_import(snapshot, keep_action=copied_action)
    finally:
        try:
            bpy.ops.object.select_all(action="DESELECT")
            for obj in original_selected:
                if obj.name in bpy.data.objects:
                    obj.select_set(True)
            if original_active is not None and original_active.name in bpy.data.objects:
                context.view_layer.objects.active = original_active
        except Exception:
            pass

    animation_data = target.animation_data_create()
    old_action = animation_data.action
    old_action_slot = getattr(animation_data, "action_slot", None)
    if old_action is not None and not bool(old_action.get(TRANSIENT_PROPERTY, False)):
        _captured_actions[target.name] = old_action
    new_scene_session = target.name not in _scene_settings
    try:
        animation_data.action = copied_action
        _bind_action_slot(animation_data, copied_action, source_slot_identifier)
    except Exception as error:
        try:
            animation_data.action = old_action
            if old_action_slot is not None and hasattr(animation_data, "action_slot"):
                animation_data.action_slot = old_action_slot
        except Exception:
            animation_data.action = None
        _remove_action(copied_action)
        scene.render.fps = previous_scene_settings[2]
        scene.render.fps_base = previous_scene_settings[3]
        raise ClipError(f"The clip Action is incompatible with armature '{target.name}': {error}") from error
    if new_scene_session:
        _scene_settings[target.name] = previous_scene_settings
    if old_action is not None and bool(old_action.get(TRANSIENT_PROPERTY, False)):
        _remove_action(old_action)

    target.data.display_type = "STICK"
    target.data.show_bone_custom_shapes = False

    start, end = copied_action.frame_range
    first_frame = int(math.floor(float(start)))
    last_frame = max(first_frame + 1, int(math.ceil(float(end))))
    scene.render.fps = 30
    scene.render.fps_base = 1.0
    scene.use_preview_range = False
    scene.frame_start = first_frame
    scene.frame_end = last_frame
    scene.frame_set(first_frame)
    _runtime_sessions[target.name] = {
        "target": target.name,
        "clip_path": str(clip_path),
        "entry": entry,
        "variant": variant,
        "frame": first_frame,
        "playing": bool(auto_play),
    }
    if auto_play:
        _start_playback()
    return copied_action


def _request_paths(library_root, request_id):
    return (
        library_root / "responses" / f"{request_id}.json",
        library_root / "requests" / f"{request_id}.response.json",
        library_root / "requests" / f"{request_id}.result.json",
    )


def _atomic_request(catalog, entry, variant, race, face, expected_path, target):
    request_id = str(uuid.uuid4())
    request_folder = (catalog["library_root"] / "requests").resolve()
    if not _inside(request_folder, catalog["default_root"]):
        raise ClipError("Animation request folder is outside XivBlend/AnimationLibrary")
    request_folder.mkdir(parents=True, exist_ok=True)
    destination = request_folder / f"{request_id}.json"
    temporary = request_folder / f".{request_id}.{uuid.uuid4().hex}.tmp"
    payload = {
        "schemaVersion": 1,
        "requestId": request_id,
        "emoteId": _entry_id(entry),
        "variantId": _variant_id(variant),
        # The cache path uses FFXIV's c0801 notation, while the C# request
        # contract intentionally serializes RaceCode as an unsigned number.
        "raceCode": int(race[1:]),
        "faceSkeleton": face,
        "gameVersion": catalog["game_version"],
    }
    try:
        with temporary.open("x", encoding="utf-8", newline="\n") as stream:
            json.dump(payload, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, destination)
    except OSError as error:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass
        raise ClipError(f"Could not queue animation extraction: {error}") from error

    _pending_requests[request_id] = {
        "created": time.monotonic(),
        "expected_path": str(expected_path),
        "target": target.name,
        "entry_id": _id_text(_entry_id(entry)),
        "variant_id": _id_text(_variant_id(variant)),
        "entry": entry,
        "variant": variant,
        "library_root": str(catalog["library_root"]),
        "response_paths": [str(path) for path in _request_paths(catalog["library_root"], request_id)],
        "import_failures": 0,
    }
    _ensure_poll_timer()
    return request_id


def _response_state(record):
    for path_text in record["response_paths"]:
        path = Path(path_text)
        if not path.is_file():
            continue
        try:
            response = _read_json(path)
        except CatalogError:
            continue  # Producer may still be atomically replacing a response.
        status = str(_field(response, "Status", default="") or "").casefold()
        if status in {"failed", "error", "rejected"}:
            return str(_field(response, "Error", "Message", default="Animation extraction failed")), None
        if status in {"ready", "complete", "completed", "success"}:
            relative = _field(response, "ClipRelativePath")
            if relative:
                try:
                    clip_path = _resolve_child(
                        Path(record["library_root"]), relative, "ClipRelativePath"
                    )
                except CatalogError as error:
                    return str(error), None
                if clip_path.suffix.casefold() not in {".glb", ".gltf"}:
                    return "The animation response did not point to a GLB/glTF clip", None
                return None, clip_path
    return None, None


def _cleanup_response_files(record):
    """Remove only this request's tiny response marker after it is consumed."""
    try:
        trusted_root = _local_library_root()
    except Exception:
        return
    for path_text in record.get("response_paths", []):
        try:
            path = Path(path_text).resolve()
            if (
                path.suffix.casefold() == ".json"
                and _inside(path, trusted_root)
                and path.parent.name in {"responses", "requests"}
            ):
                path.unlink(missing_ok=True)
        except OSError:
            pass


def _poll_requests():
    if not _pending_requests:
        return None
    now = time.monotonic()
    for request_id, record in list(_pending_requests.items()):
        if now - record["created"] > REQUEST_TIMEOUT_SECONDS:
            _pending_requests.pop(request_id, None)
            _cleanup_response_files(record)
            _set_status(f"Timed out waiting for {_entry_name(record['entry'])}. Try Refresh and play it again.")
            continue
        error, response_clip = _response_state(record)
        if error:
            _pending_requests.pop(request_id, None)
            _cleanup_response_files(record)
            _set_status(error)
            continue

        clip_path = response_clip or Path(record["expected_path"])
        if not clip_path.is_file():
            # A catalog replacement can publish a changed deterministic path.
            catalog, _ = _catalog_or_error(force=False)
            if catalog is not None:
                entry = _find_entry(catalog, record["entry_id"])
                variant = _find_variant(entry, record["variant_id"]) if entry is not None else None
                target = bpy.data.objects.get(record["target"])
                if variant is not None and target is not None:
                    try:
                        kind = str(_field(variant, "Kind", default="Body") or "Body")
                        race, face = _rig_identity(
                            bpy.context, target, require_face=kind.casefold() == "face"
                        )
                        clip_path = _format_clip_path(catalog, entry, variant, race, face)
                        record["expected_path"] = str(clip_path)
                        record["entry"] = entry
                        record["variant"] = variant
                    except Exception:
                        pass
        if not clip_path.is_file():
            continue

        target = bpy.data.objects.get(record["target"])
        if target is None or target.type != "ARMATURE":
            _pending_requests.pop(request_id, None)
            _cleanup_response_files(record)
            _set_status("The target armature was removed before the animation was ready")
            continue
        try:
            _import_clip(bpy.context, target, clip_path, record["entry"], record["variant"], auto_play=True)
        except Exception as error:
            record["import_failures"] += 1
            if record["import_failures"] >= 3:
                _pending_requests.pop(request_id, None)
                _cleanup_response_files(record)
                _set_status(f"Could not import {_entry_name(record['entry'])}: {error}")
            continue
        _pending_requests.pop(request_id, None)
        _cleanup_response_files(record)
        _set_status(f"Playing {_entry_name(record['entry'])} on a loop")
    return POLL_SECONDS if _pending_requests else None


def _ensure_poll_timer():
    try:
        if not bpy.app.timers.is_registered(_poll_requests):
            bpy.app.timers.register(_poll_requests, first_interval=POLL_SECONDS, persistent=True)
    except Exception:
        bpy.app.timers.register(_poll_requests, first_interval=POLL_SECONDS, persistent=True)


def _play(context, emote_id, variant_id=""):
    catalog = _load_catalog()
    entry = _find_entry(catalog, emote_id)
    if entry is None:
        raise ClipError(f"Emote {emote_id} is no longer in the animation catalog")
    variant = _find_variant(entry, variant_id)
    if variant is None:
        raise ClipError(f"'{_entry_name(entry)}' has no playable variant")
    kind = str(_field(variant, "Kind", default="Body") or "Body")
    target = _target_armature(context, kind)
    if target is None:
        raise ClipError("No armature was found. Open an XivBlend character file first.")
    race, face = _rig_identity(context, target, require_face=kind.casefold() == "face")
    clip_path = _format_clip_path(catalog, entry, variant, race, face)
    if clip_path.is_file():
        _import_clip(context, target, clip_path, entry, variant, auto_play=True)
        return f"Playing {_entry_name(entry)} on a loop"

    duplicate = next((
        request_id for request_id, record in _pending_requests.items()
        if record["expected_path"] == str(clip_path) and record["target"] == target.name
    ), None)
    if duplicate:
        return f"Waiting for {_entry_name(entry)} (request {duplicate[:8]})"
    request_id = _atomic_request(catalog, entry, variant, race, face, clip_path, target)
    return f"Preparing {_entry_name(entry)}… request {request_id[:8]}"


def _wrap_lines(text, width=48):
    words = str(text).split()
    lines, current = [], []
    for word in words:
        if current and len(" ".join(current + [word])) > width:
            lines.append(" ".join(current))
            current = [word]
        else:
            current.append(word)
    if current:
        lines.append(" ".join(current))
    return lines or [""]


def _reset_page(_self, context):
    try:
        context.scene.xivblend_animation_page = 0
    except Exception:
        pass


class XIVBLEND_OT_refresh_animations(Operator):
    bl_idname = "xivblend.refresh_animations"
    bl_label = "Refresh Animation Library"
    bl_description = "Reload XivBlend's local emote catalog and icons"

    def execute(self, context):
        catalog, error = _catalog_or_error(force=True)
        if error:
            _set_status(error)
            self.report({"ERROR"}, error)
            return {"CANCELLED"}
        context.scene.xivblend_animation_page = 0
        message = f"Loaded {len(catalog['entries'])} player emotes"
        _set_status(message)
        self.report({"INFO"}, message)
        return {"FINISHED"}


class XIVBLEND_OT_set_animation_category(Operator):
    bl_idname = "xivblend.set_animation_category"
    bl_label = "Set Animation Category"

    category: StringProperty(options={"SKIP_SAVE"})

    def execute(self, context):
        context.scene.xivblend_animation_category = self.category
        context.scene.xivblend_animation_page = 0
        return {"FINISHED"}


class XIVBLEND_MT_animation_categories(Menu):
    bl_idname = "XIVBLEND_MT_animation_categories"
    bl_label = "Emote Category"

    def draw(self, context):
        layout = self.layout
        catalog, error = _catalog_or_error()
        operator = layout.operator(XIVBLEND_OT_set_animation_category.bl_idname, text="All categories")
        operator.category = "__ALL__"
        if error:
            return
        categories = sorted({_entry_category(entry) for entry in catalog["entries"]}, key=str.casefold)
        for category in categories:
            operator = layout.operator(XIVBLEND_OT_set_animation_category.bl_idname, text=category)
            operator.category = category


class XIVBLEND_OT_change_animation_page(Operator):
    bl_idname = "xivblend.change_animation_page"
    bl_label = "Change Animation Page"

    delta: IntProperty(default=0, options={"SKIP_SAVE"})

    def execute(self, context):
        catalog, error = _catalog_or_error()
        if error:
            return {"CANCELLED"}
        entries = _filtered_entries(context.scene, catalog)
        last_page = max(0, math.ceil(len(entries) / PAGE_SIZE) - 1)
        context.scene.xivblend_animation_page = min(
            last_page, max(0, context.scene.xivblend_animation_page + self.delta)
        )
        return {"FINISHED"}


class XIVBLEND_OT_play_emote(Operator):
    bl_idname = "xivblend.play_emote"
    bl_label = "Play Emote"
    # Runtime previews are deliberately outside Blender's undo stack; otherwise
    # browsing many emotes would retain hidden copies and defeat the light cache.
    bl_options = {"REGISTER"}

    emote_id: StringProperty(options={"SKIP_SAVE"})
    variant_id: StringProperty(options={"SKIP_SAVE"})

    @classmethod
    def description(cls, _context, properties):
        catalog, _ = _catalog_or_error()
        entry = _find_entry(catalog, properties.emote_id) if catalog else None
        if entry is None:
            return "Play this emote"
        command = _entry_command(entry)
        return f"Play {_entry_name(entry)}{f' ({command})' if command else ''} on a loop"

    def execute(self, context):
        try:
            message = _play(context, self.emote_id, self.variant_id)
        except Exception as error:
            message = str(error)
            _set_status(message)
            self.report({"ERROR"}, message)
            return {"CANCELLED"}
        _set_status(message)
        self.report({"INFO"}, message)
        return {"FINISHED"}


class XIVBLEND_OT_restore_captured_pose(Operator):
    bl_idname = "xivblend.restore_captured_pose"
    bl_label = "Stop / Restore Captured Pose"
    bl_description = "Stop playback, unload the runtime emote, and return to the exported captured pose"
    bl_options = {"REGISTER"}

    def execute(self, context):
        _stop_playback()
        restored = False
        for target_name in list(_runtime_sessions):
            target = bpy.data.objects.get(target_name)
            if target is not None:
                _restore_target(target, restore_scene=True)
                restored = True
        if not restored:
            target = _target_armature(context)
            if target is not None:
                _restore_target(target, restore_scene=True)
        _purge_transient_actions(restore=True)
        _set_status("Stopped; captured pose restored")
        return {"FINISHED"}


class XIVBLEND_PT_animation_browser(Panel):
    bl_idname = "XIVBLEND_PT_animation_browser"
    bl_label = "Player Emotes"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "XivBlend"

    def draw(self, context):
        layout = self.layout
        scene = context.scene
        catalog, error = _catalog_or_error()

        header = layout.row(align=True)
        header.label(text="Animation Library", icon="ARMATURE_DATA")
        header.operator(XIVBLEND_OT_refresh_animations.bl_idname, text="", icon="FILE_REFRESH")

        if error:
            box = layout.box()
            for index, line in enumerate(_wrap_lines(error)):
                box.label(text=line, icon="ERROR" if index == 0 else "NONE")
            box.label(text="Export or refresh the library from FFXIV, then retry.")
            return

        target = _target_armature(context)
        target_row = layout.row()
        target_row.label(text=f"Rig: {target.name}" if target else "No character rig found", icon="OUTLINER_OB_ARMATURE")
        layout.prop(scene, "xivblend_animation_search", text="", icon="VIEWZOOM")

        category = scene.xivblend_animation_category
        category_label = "All categories" if not category or category == "__ALL__" else category
        layout.menu(XIVBLEND_MT_animation_categories.bl_idname, text=category_label, icon="FILTER")

        entries = _filtered_entries(scene, catalog)
        page_count = max(1, math.ceil(len(entries) / PAGE_SIZE))
        page = min(max(0, scene.xivblend_animation_page), page_count - 1)
        visible = entries[page * PAGE_SIZE:(page + 1) * PAGE_SIZE]
        if not visible:
            layout.label(text="No matching emotes", icon="INFO")
        else:
            grid = layout.grid_flow(row_major=True, columns=3, even_columns=True, even_rows=True, align=True)
            for entry in visible:
                icon_value = _icon_value(catalog, entry)
                kwargs = {"text": _entry_name(entry)[:22]}
                if icon_value:
                    kwargs["icon_value"] = icon_value
                else:
                    kwargs["icon"] = "PLAY"
                operator = grid.operator(XIVBLEND_OT_play_emote.bl_idname, **kwargs)
                operator.emote_id = _id_text(_entry_id(entry))
                operator.variant_id = ""

        pages = layout.row(align=True)
        previous = pages.operator(XIVBLEND_OT_change_animation_page.bl_idname, text="", icon="TRIA_LEFT")
        previous.delta = -1
        pages.label(text=f"{len(entries)} emotes  •  Page {page + 1}/{page_count}")
        following = pages.operator(XIVBLEND_OT_change_animation_page.bl_idname, text="", icon="TRIA_RIGHT")
        following.delta = 1

        layout.separator()
        layout.operator(XIVBLEND_OT_restore_captured_pose.bl_idname, icon="PAUSE")
        status = getattr(context.window_manager, "xivblend_animation_status", "") or _status
        status_box = layout.box()
        for line in _wrap_lines(status):
            status_box.label(text=line)


@persistent
def _save_pre_handler(_filepath):
    global _save_sessions
    _save_sessions = []
    _stop_playback()
    for target_name, session in list(_runtime_sessions.items()):
        target = bpy.data.objects.get(target_name)
        if target is None:
            continue
        saved = dict(session)
        saved["frame"] = int(bpy.context.scene.frame_current)
        _save_sessions.append(saved)
        # The file on disk should reopen exactly like a normal XivBlend export:
        # captured-pose Action active, frame 100 selected, and its original range.
        _restore_target(target, restore_scene=True)
    _purge_transient_actions(restore=True)


def _resume_after_save():
    global _save_sessions
    sessions, _save_sessions = _save_sessions, []
    for session in sessions:
        target = bpy.data.objects.get(session["target"])
        clip_path = Path(session["clip_path"])
        if target is None or not clip_path.is_file():
            continue
        try:
            _import_clip(
                bpy.context, target, clip_path, session["entry"], session["variant"],
                auto_play=bool(session.get("playing", False)),
            )
            bpy.context.scene.frame_set(min(
                bpy.context.scene.frame_end,
                max(bpy.context.scene.frame_start, int(session.get("frame", bpy.context.scene.frame_start))),
            ))
        except Exception as error:
            _set_status(f"Saved safely, but could not resume the preview: {error}")
    return None


@persistent
def _save_post_handler(_filepath):
    if _save_sessions:
        bpy.app.timers.register(_resume_after_save, first_interval=0.1)


@persistent
def _load_post_handler(_filepath):
    global _catalog, _catalog_signature, _save_sessions
    _stop_playback()
    _purge_transient_actions(restore=True)
    _pending_requests.clear()
    _captured_actions.clear()
    _scene_settings.clear()
    _runtime_sessions.clear()
    _save_sessions = []
    _catalog = None
    _catalog_signature = None
    _close_previews()
    _set_status("Ready")


_CLASSES = (
    XIVBLEND_OT_refresh_animations,
    XIVBLEND_OT_set_animation_category,
    XIVBLEND_MT_animation_categories,
    XIVBLEND_OT_change_animation_page,
    XIVBLEND_OT_play_emote,
    XIVBLEND_OT_restore_captured_pose,
    XIVBLEND_PT_animation_browser,
)


def register():
    for cls in _CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.Scene.xivblend_animation_search = StringProperty(
        name="Search player emotes",
        description="Filter by emote name, slash command, or category",
        default="",
        update=_reset_page,
        options={"SKIP_SAVE"},
    )
    bpy.types.Scene.xivblend_animation_category = StringProperty(
        name="Emote category",
        default="__ALL__",
        options={"SKIP_SAVE"},
    )
    bpy.types.Scene.xivblend_animation_page = IntProperty(
        name="Emote page",
        default=0,
        min=0,
        options={"SKIP_SAVE"},
    )
    bpy.types.WindowManager.xivblend_animation_status = StringProperty(
        name="XivBlend animation status",
        default="Ready",
        options={"SKIP_SAVE"},
    )
    for handlers, callback in (
        (bpy.app.handlers.save_pre, _save_pre_handler),
        (bpy.app.handlers.save_post, _save_post_handler),
        (bpy.app.handlers.load_post, _load_post_handler),
    ):
        if callback not in handlers:
            handlers.append(callback)


def unregister():
    global _save_sessions
    _stop_playback()
    for target_name in list(_runtime_sessions):
        target = bpy.data.objects.get(target_name)
        if target is not None:
            _restore_target(target, restore_scene=True)
    _purge_transient_actions(restore=True)
    _pending_requests.clear()
    try:
        if bpy.app.timers.is_registered(_poll_requests):
            bpy.app.timers.unregister(_poll_requests)
    except Exception:
        pass
    try:
        if bpy.app.timers.is_registered(_resume_after_save):
            bpy.app.timers.unregister(_resume_after_save)
    except Exception:
        pass
    _save_sessions = []
    for handlers, callback in (
        (bpy.app.handlers.save_pre, _save_pre_handler),
        (bpy.app.handlers.save_post, _save_post_handler),
        (bpy.app.handlers.load_post, _load_post_handler),
    ):
        if callback in handlers:
            handlers.remove(callback)
    for owner, name in (
        (bpy.types.Scene, "xivblend_animation_search"),
        (bpy.types.Scene, "xivblend_animation_category"),
        (bpy.types.Scene, "xivblend_animation_page"),
        (bpy.types.WindowManager, "xivblend_animation_status"),
    ):
        if hasattr(owner, name):
            delattr(owner, name)
    for cls in reversed(_CLASSES):
        bpy.utils.unregister_class(cls)
    _close_previews()


if __name__ == "__main__":
    register()
