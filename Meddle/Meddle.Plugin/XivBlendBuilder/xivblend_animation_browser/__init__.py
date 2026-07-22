"""XivBlend's lightweight animation browser and portrait tools for Blender.

The add-on intentionally contains no FFXIV assets.  It reads a local catalog
created by the XivBlend Dalamud plugin and asks that plugin to build a missing
clip.  Imported Actions are runtime-only: save handlers restore the captured
pose and remove those Actions before Blender writes a .blend file.
"""

bl_info = {
    "name": "XivBlend Animation Browser",
    "author": "XivBlend contributors",
    "version": (0, 7, 1),
    "blender": (5, 0, 0),
    "location": "3D View > Sidebar > XivBlend",
    "description": "Browse FFXIV player emotes and frame portrait renders",
    "category": "Animation",
}

import json
import hashlib
import importlib
import math
import os
from pathlib import Path
import sys
import time
import types
import uuid

import bpy
from bpy.app.handlers import persistent
from bpy.props import EnumProperty, IntProperty, StringProperty
from bpy.types import Menu, Operator, Panel
from mathutils import Euler, Matrix, Vector

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
TRANSIENT_NLA_PREFIX = "XivBlend Runtime |"
CAPTURED_ACTION_PREFIX = "XivBlend | A-Pose to Captured Pose"
CAPTURED_MARKER = "CAPTURED POSE"
STUDIO_COMPONENT_PROPERTY = "xivblend_component"
STUDIO_CAMERA_COMPONENT = "studio_camera"
CUSTOM_BONE_SHAPE_PROPERTY = "xivblend_custom_bone_shape"
GLTF_BONE_AXIS_CORRECTION_PROPERTY = "xivblend_gltf_axis_correction_v1"
LEGACY_GLTF_SOCKET_BONES = frozenset(
    {"n_buki_l", "n_buki_r", "n_throw", "j_te_l", "j_te_r"}
)
SCENE_SETUP_COLLECTION = "Scene Setup"
CHARACTER_COLLECTION = "FFXIV Character"
CHARACTER_MESH_COMPONENT = "Meshes"
MAX_CAMERA_ACTION_SAMPLES = 96
MAX_JSON_BYTES = 16 * 1024 * 1024
MAX_CATALOG_ENTRIES = 20_000
MAX_BUNDLE_LAYERS = 4_096
MAX_BUNDLE_VISUALS = 65_536
MAX_BUNDLE_PROPS = 16_384
MAX_AVFX_BYTES = 32 * 1024 * 1024
PROP_CACHE_MANIFEST = "prop-cache-v1.json"
MAX_PROP_CACHE_FILE_BYTES = 512 * 1024 * 1024
MAX_PROP_CACHE_TOTAL_BYTES = 8 * 1024 * 1024 * 1024
STUDIO_BACKDROP_COMPONENT = "studio_backdrop"
STUDIO_LIGHT_COMPONENT = "studio_light"
STUDIO_BACKGROUND_RAMP = "XivBlend Background Gradient"
LEGACY_PREVIEW_MATERIAL_NAME = "XivBlend Smooth Animation Preview"
LEGACY_PREVIEW_MATERIAL_PROPERTY = "xivblend_runtime_preview_material"
RUNTIME_EFFECT_COLLECTION_PREFIX = "XivBlend Runtime Effects |"
MATERIAL_RUNTIME_MODULE = "_xivblend_meddle_material_runtime"
SYNC_CONTROL_VFX = "vfx/common/eff/syncactiontimelineclip01t.avfx"

_catalog = None
_catalog_signature = None
_preview_collection = None
_preview_paths = {}
_pending_requests = {}
_captured_actions = {}
_scene_settings = {}
_action_settings = {}
_runtime_sessions = {}
_save_sessions = []
_status = "Ready"
_render_status = "Choose a render mode, fit the camera, then render the current frame."


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


def _number(value, fallback, caster):
    try:
        return caster(value)
    except (TypeError, ValueError, OverflowError):
        return fallback


def _int_value(value, fallback=0):
    return _number(value, fallback, int)


def _float_value(value, fallback=0.0):
    return _number(value, fallback, float)


def _read_json(path):
    try:
        size = path.stat().st_size
        if size <= 0 or size > MAX_JSON_BYTES:
            raise CatalogError(
                f"{path.name} has invalid size {size} bytes (limit {MAX_JSON_BYTES} bytes)"
            )
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
    if len(entries) > MAX_CATALOG_ENTRIES:
        raise CatalogError(
            f"{catalog_path.name} contains too many entries ({len(entries)})"
        )

    usable_entries = []
    for raw_entry in entries:
        if not isinstance(raw_entry, dict):
            continue
        entry_id = _field(raw_entry, "EntryId", "EmoteId", "Id")
        name = str(_field(raw_entry, "Name", default="") or "").strip()
        variants = [item for item in _as_list(_field(raw_entry, "Variants")) if isinstance(item, dict)]
        if entry_id is None or not str(entry_id).strip() or not name or not variants:
            continue
        usable_entries.append(raw_entry)

    usable_entries.sort(key=lambda item: (
        str(_field(item, "Category", default="Other") or "Other").casefold(),
        str(_field(item, "Name", default="")).casefold(),
        _id_text(_field(item, "EntryId", "EmoteId", "Id")),
    ))
    game_version = str(
        _field(document, "GameVersion", default=_field(current, "GameVersion", default="")) or ""
    ).strip()
    _catalog = {
        "document": document,
        "entries": usable_entries,
        "game_version": game_version,
        "schema_version": max(
            1,
            _int_value(
                _field(document, "SchemaVersion", default=_field(current, "SchemaVersion", default=1)),
                1,
            ),
        ),
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
    return _field(entry, "EntryId", "EmoteId", "Id")


def _entry_emote_id(entry):
    return _field(entry, "EmoteId", "Id")


def _entry_name(entry):
    return str(_field(entry, "Name", default="Unnamed emote") or "Unnamed emote")


def _entry_command(entry):
    return str(_field(entry, "Command", default="") or "")


def _entry_category(entry):
    return str(_field(entry, "Category", default="Other") or "Other")


def _entry_source_kind(entry):
    return str(_field(entry, "SourceKind", default="Vanilla") or "Vanilla").strip()


def _entry_source_name(entry):
    return str(_field(entry, "SourceDisplayName", default="") or "").strip()


def _entry_source_badge(entry):
    source = _entry_source_kind(entry).casefold()
    if source in {"", "vanilla", "game", "builtin", "built-in"}:
        return ""
    if source in {
        "custom", "mod", "penumbra", "penumbramod", "penumbra mod",
        "modoverride", "mod override",
    }:
        return "MOD"
    cleaned = "".join(character for character in _entry_source_kind(entry).upper() if character.isalnum())
    return cleaned[:6] or "CUSTOM"


def _entry_source_description(entry):
    badge = _entry_source_badge(entry)
    if not badge:
        return "FFXIV game animation"
    name = _entry_source_name(entry)
    return f"{badge}: {name}" if name else f"{badge} animation source"


def _variant_id(variant):
    return _field(variant, "VariantId", "Id")


def _find_entry(catalog, emote_id):
    wanted = _id_text(emote_id)
    return next((entry for entry in catalog["entries"] if _id_text(_entry_id(entry)) == wanted), None)


def _race_number(race):
    text = str(race or "").strip().lower()
    if text.startswith("c"):
        text = text[1:]
    try:
        return int(text)
    except (TypeError, ValueError):
        return None


def _variant_supports_race(variant, race):
    configured = _field(variant, "CompatibleRaceCodes", default=None)
    if configured is None:
        return True
    wanted = _race_number(race)
    if wanted is None:
        return False
    supported = {
        value for item in _as_list(configured)
        if (value := _race_number(item)) is not None
    }
    return wanted in supported


def _entry_supports_race(entry, race):
    return any(
        _variant_supports_race(variant, race)
        for variant in _as_list(_field(entry, "Variants"))
        if isinstance(variant, dict)
    )


def _find_variant(entry, variant_id="", race=""):
    variants = [
        variant for variant in _as_list(_field(entry, "Variants"))
        if isinstance(variant, dict) and (not race or _variant_supports_race(variant, race))
    ]
    requested = variant_id or _field(entry, "DefaultVariantId", default="")
    if requested:
        wanted = _id_text(requested)
        match = next((variant for variant in variants if _id_text(_variant_id(variant)) == wanted), None)
        if match is not None:
            return match
        if race:
            return None
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
        (obj for obj in context.scene.objects if obj.type == "ARMATURE"),
        key=lambda obj: obj.name.casefold(),
    )
    if not armatures:
        return None
    if active not in armatures:
        active = None
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


def _template_values(entry, variant, race, face):
    face_key = face or "noface"
    return {
        "race": race,
        "Race": race,
        "raceCode": race,
        "RaceCode": race,
        "face": face,
        "Face": face,
        "faceSkeleton": face,
        "FaceSkeleton": face,
        "faceKey": face_key,
        "FaceKey": face_key,
        "entryId": _entry_id(entry),
        "EntryId": _entry_id(entry),
        "emoteId": _entry_emote_id(entry),
        "EmoteId": _entry_emote_id(entry),
        "variantId": _variant_id(variant),
        "VariantId": _variant_id(variant),
    }


def _format_template_path(catalog, entry, variant, race, face, template, label, suffixes):
    try:
        relative = str(template).format_map(_template_values(entry, variant, race, face))
    except (KeyError, ValueError) as error:
        raise ClipError(f"Invalid {label} for '{_entry_name(entry)}': {error}") from error
    candidate = _resolve_child(catalog["catalog_parent"], relative, label)
    if candidate.suffix.casefold() not in suffixes:
        expected = "/".join(sorted(suffixes))
        raise ClipError(f"{label} must point to {expected}: {candidate.name}")
    return candidate


def _format_clip_path(catalog, entry, variant, race, face):
    template = _field(variant, "CacheRelativePathTemplate", "CacheRelativePath")
    if not template:
        raise ClipError(f"'{_entry_name(entry)}' has no CacheRelativePathTemplate")
    return _format_template_path(
        catalog, entry, variant, race, face, template,
        "CacheRelativePathTemplate", {".glb", ".gltf"},
    )


def _format_bundle_path(catalog, entry, variant, race, face):
    template = _field(variant, "BundleRelativePathTemplate", "BundleRelativePath")
    if not template:
        return None
    return _format_template_path(
        catalog, entry, variant, race, face, template,
        "BundleRelativePathTemplate", {".json"},
    )


def _expected_animation_asset(catalog, entry, variant, race, face):
    bundle_path = _format_bundle_path(catalog, entry, variant, race, face)
    if bundle_path is not None:
        return "bundle", bundle_path
    return "clip", _format_clip_path(catalog, entry, variant, race, face)


def _read_bundle_manifest(catalog, bundle_path):
    bundle_path = Path(bundle_path).resolve()
    if not _inside(bundle_path, catalog["default_root"]):
        raise ClipError("Animation bundle is outside XivBlend/AnimationLibrary")
    document = _read_json(bundle_path)
    if not isinstance(document, dict):
        raise ClipError(f"{bundle_path.name} must contain a JSON object")
    schema = _int_value(_field(document, "SchemaVersion"), 0)
    if schema != 3:
        raise ClipError(
            f"Animation bundle {bundle_path.name} uses obsolete schema {schema}; "
            "rebuild it with the current XivBlend plugin"
        )
    layers = _field(document, "Layers")
    if not isinstance(layers, list) or not any(isinstance(layer, dict) for layer in layers):
        raise ClipError(f"Animation bundle {bundle_path.name} contains no playable layers")
    visuals = _field(document, "VisualEffects", default=[])
    props = _field(document, "Props", default=[])
    warnings = _field(document, "Warnings", default=[])
    if len(layers) > MAX_BUNDLE_LAYERS:
        raise ClipError(f"Animation bundle contains too many layers ({len(layers)})")
    if not isinstance(visuals, list) or len(visuals) > MAX_BUNDLE_VISUALS:
        raise ClipError("Animation bundle contains an invalid visual-event list")
    if not isinstance(props, list) or len(props) > MAX_BUNDLE_PROPS:
        raise ClipError("Animation bundle contains an invalid prop-event list")
    if not isinstance(warnings, list) or len(warnings) > 4_096:
        raise ClipError("Animation bundle contains an invalid warning list")
    frame_start = _int_value(_field(document, "FrameStart"), -1)
    frame_end = _int_value(_field(document, "FrameEnd"), -1)
    if frame_start < 0 or frame_end <= frame_start or frame_end > 10_000_000:
        raise ClipError("Animation bundle has an invalid frame range")
    for layer in layers:
        if not isinstance(layer, dict):
            raise ClipError("Animation bundle contains a malformed layer")
        relative = str(_field(layer, "ClipRelativePath", default="") or "")
        start = _int_value(_field(layer, "StartFrame"), -1)
        duration = _int_value(_field(layer, "DurationFrames"), -1)
        source_start = _float_value(_field(layer, "SourceStartFrame"), math.nan)
        source_end = _float_value(_field(layer, "SourceEndFrame"), math.nan)
        if (
            not relative or len(relative) > 32_768
            or start < frame_start or duration <= 0 or start + duration > frame_end
            or not math.isfinite(source_start) or not math.isfinite(source_end)
            or source_start < 0.0 or source_end < source_start
        ):
            raise ClipError("Animation bundle contains an invalid timed layer")
    return document


def _bundle_layer_path(catalog, layer):
    relative = _field(layer, "ClipRelativePath")
    try:
        path = _resolve_child(catalog["library_root"], relative, "ClipRelativePath")
    except CatalogError as error:
        raise ClipError(str(error)) from error
    if path.suffix.casefold() not in {".glb", ".gltf"}:
        raise ClipError(f"Animation layer must be a .glb or .gltf file: {path.name}")
    return path


def _validate_bundle_identity(manifest, catalog, entry, variant):
    if _id_text(_field(manifest, "EntryId")) != _id_text(_entry_id(entry)):
        raise ClipError("Animation bundle entry identity does not match the selected card")
    if _id_text(_field(manifest, "VariantId")) != _id_text(_variant_id(variant)):
        raise ClipError("Animation bundle variant identity does not match the selected card")
    bundle_game = str(_field(manifest, "GameVersion", default="") or "").strip()
    if bundle_game != str(catalog.get("game_version", "")).strip():
        raise ClipError("Animation bundle game version does not match the active catalog")


def _valid_animation_layer_file(path):
    try:
        if not path.is_file():
            return False
        if path.suffix.casefold() != ".glb":
            return True
        size = path.stat().st_size
        if size < 20 or size > 512 * 1024 * 1024:
            return False
        with path.open("rb") as stream:
            header = stream.read(12)
        return (
            len(header) == 12 and header[:4] == b"glTF"
            and int.from_bytes(header[4:8], "little") == 2
            and int.from_bytes(header[8:12], "little") == size
        )
    except OSError:
        return False


def _sha256_file(path, hash_cache):
    key = str(path)
    actual_hash = hash_cache.get(key)
    if actual_hash is None:
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        actual_hash = digest.hexdigest()
        hash_cache[key] = actual_hash
    return actual_hash


def _valid_prop_cache_integrity(asset_path, cache_path, hash_cache):
    manifest_path = (asset_path.parent / PROP_CACHE_MANIFEST).resolve()
    if not _inside(manifest_path, asset_path.parent):
        return False
    document = _read_json(manifest_path)
    if not isinstance(document, dict) or _int_value(
        _field(document, "SchemaVersion"), 0
    ) != 1:
        return False
    asset_hash = str(_field(document, "AssetSha256", default="") or "").strip()
    files = _field(document, "Files")
    if (
        len(asset_hash) != 64
        or asset_hash != asset_hash.casefold()
        or any(character not in "0123456789abcdef" for character in asset_hash)
        or not isinstance(files, list)
        or not 1 <= len(files) <= MAX_BUNDLE_PROPS
        or _sha256_file(asset_path, hash_cache) != asset_hash
    ):
        return False

    seen = set()
    total_bytes = 0
    for item in files:
        if not isinstance(item, dict):
            return False
        relative = str(_field(item, "RelativePath", default="") or "").strip()
        expected_length = _field(item, "Length")
        expected_hash = str(_field(item, "Sha256", default="") or "").strip()
        folded = relative.casefold()
        if (
            not relative
            or len(relative) > 32_768
            or Path(relative).is_absolute()
            or "\\" in relative
            or ":" in relative
            or any(segment in {"", ".", ".."} for segment in relative.split("/"))
            or folded in seen
            or isinstance(expected_length, bool)
            or not isinstance(expected_length, int)
            or not 0 < expected_length <= MAX_PROP_CACHE_FILE_BYTES
            or len(expected_hash) != 64
            or expected_hash != expected_hash.casefold()
            or any(character not in "0123456789abcdef" for character in expected_hash)
        ):
            return False
        seen.add(folded)
        total_bytes += expected_length
        if total_bytes > MAX_PROP_CACHE_TOTAL_BYTES:
            return False
        file_path = _resolve_child(cache_path, relative, "prop cache file")
        if (
            not file_path.is_file()
            or file_path.stat().st_size != expected_length
            or _sha256_file(file_path, hash_cache) != expected_hash
        ):
            return False
    return True


def _valid_bundle_prop_asset(catalog, event, hash_cache):
    status = str(_field(event, "AssetStatus", default="") or "").strip().casefold()
    if status == "exportfailed":
        return False
    if status != "ready":
        return status in {"missingmodel", "unsupportedkind"}
    try:
        asset_path, cache_path = _resolve_prop_asset(catalog, event)
        return (
            _valid_animation_layer_file(asset_path)
            and cache_path.is_dir()
            and _valid_prop_cache_integrity(asset_path, cache_path, hash_cache)
        )
    except (CatalogError, ClipError, OSError):
        return False


def _valid_bundle_vfx_asset(catalog, event, hash_cache):
    status = str(_field(event, "AssetStatus", default="") or "").strip().casefold()
    source_relative = str(
        _field(event, "SourceRelativePath", default="") or ""
    ).strip()
    preview_relative = str(
        _field(event, "StaticPreviewRelativePath", default="") or ""
    ).strip()
    preview_hash_value = str(
        _field(event, "StaticPreviewSha256", default="") or ""
    ).strip()

    if status == "exportfailed":
        return False
    if status in {"synccontrol", "missingasset", "analysisfailed"}:
        return not source_relative and not preview_relative and not preview_hash_value
    if status not in {
        "staticembeddedmeshpreview", "unsupportedapricot", "metadataonly"
    }:
        return False

    expected_hash = str(_field(event, "ContentSha256", default="") or "").strip()
    if (
        len(expected_hash) != 64
        or expected_hash != expected_hash.casefold()
        or any(character not in "0123456789abcdef" for character in expected_hash)
        or not source_relative
    ):
        return False
    try:
        source_path = _resolve_child(
            catalog["library_root"], source_relative, "SourceRelativePath"
        )
        if source_path.suffix.casefold() != ".avfx" or not source_path.is_file():
            return False
        size = source_path.stat().st_size
        if size < 8 or size > MAX_AVFX_BYTES:
            return False
        if _sha256_file(source_path, hash_cache) != expected_hash:
            return False

        if status == "staticembeddedmeshpreview":
            if not preview_relative:
                return False
            preview_hash = preview_hash_value
            if (
                len(preview_hash) != 64
                or preview_hash != preview_hash.casefold()
                or any(character not in "0123456789abcdef" for character in preview_hash)
            ):
                return False
            preview_path = _resolve_child(
                catalog["library_root"], preview_relative,
                "StaticPreviewRelativePath",
            )
            return (
                preview_path.suffix.casefold() == ".glb"
                and _valid_animation_layer_file(preview_path)
                and _sha256_file(preview_path, hash_cache) == preview_hash
            )
        return (
            not preview_relative
            and not preview_hash_value
        )
    except (CatalogError, OSError):
        return False


def _bundle_is_ready(catalog, bundle_path, entry, variant):
    try:
        manifest = _read_bundle_manifest(catalog, bundle_path)
        _validate_bundle_identity(manifest, catalog, entry, variant)
        if not all(
            _valid_animation_layer_file(_bundle_layer_path(catalog, layer))
            for layer in _as_list(_field(manifest, "Layers"))
        ):
            return False
        hash_cache = {}
        props = _as_list(_field(manifest, "Props"))
        if any(not isinstance(event, dict) for event in props) or not all(
            _valid_bundle_prop_asset(catalog, event, hash_cache) for event in props
        ):
            return False
        visual_effects = _as_list(_field(manifest, "VisualEffects"))
        if any(not isinstance(event, dict) for event in visual_effects):
            return False
        return all(
            _valid_bundle_vfx_asset(catalog, event, hash_cache)
            for event in visual_effects
        )
    except (CatalogError, ClipError, OSError):
        return False


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


def _filtered_entries(context, catalog):
    scene = context.scene
    search = str(getattr(scene, "xivblend_animation_search", "") or "").strip().casefold()
    category = str(getattr(scene, "xivblend_animation_category", "") or "")
    race = ""
    target = _target_armature(context)
    if target is not None:
        try:
            race, _ = _rig_identity(context, target, require_face=False)
        except ClipError:
            pass
    result = []
    for entry in catalog["entries"]:
        if race and not _entry_supports_race(entry, race):
            continue
        if category and category != "__ALL__" and _entry_category(entry) != category:
            continue
        searchable = " ".join((
            _entry_name(entry), _entry_command(entry), _entry_category(entry),
            _entry_source_kind(entry), _entry_source_name(entry),
        )).casefold()
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


def _remove_runtime_visuals(target=None):
    target_name = target.name if target is not None else None
    collections = [
        collection for collection in bpy.data.collections
        if bool(collection.get(TRANSIENT_PROPERTY, False))
        and str(collection.name).startswith(RUNTIME_EFFECT_COLLECTION_PREFIX)
        and (
            target_name is None
            or str(collection.get("xivblend_runtime_target", "")) == target_name
        )
    ]
    for collection in collections:
        orphan_data = []
        orphan_actions = []
        orphan_materials = []
        for obj in list(collection.all_objects):
            if obj.animation_data is not None and obj.animation_data.action is not None:
                orphan_actions.append(obj.animation_data.action)
            if getattr(obj, "data", None) is not None:
                orphan_data.append(obj.data)
            for slot in getattr(obj, "material_slots", ()):
                if slot.material is not None:
                    orphan_materials.append(slot.material)
            try:
                bpy.data.objects.remove(obj, do_unlink=True)
            except (ReferenceError, RuntimeError):
                pass
        try:
            bpy.data.collections.remove(collection)
        except (ReferenceError, RuntimeError):
            pass
        for action in orphan_actions:
            _remove_action(action)
        for data_block in orphan_data:
            try:
                collection_name = data_block.bl_rna.identifier.lower() + "s"
                data_collection = getattr(bpy.data, collection_name, None)
                if data_collection is not None and data_block.users == 0:
                    data_collection.remove(data_block)
            except (AttributeError, ReferenceError, RuntimeError):
                pass
        for material in orphan_materials:
            try:
                if material.users == 0 and bool(material.get(TRANSIENT_PROPERTY, False)):
                    bpy.data.materials.remove(material)
            except (ReferenceError, RuntimeError):
                pass
    for data_collection in (
        bpy.data.meshes,
        bpy.data.materials,
        bpy.data.images,
        bpy.data.textures,
        bpy.data.node_groups,
    ):
        for data_block in list(data_collection):
            try:
                if data_block.users == 0 and bool(data_block.get(TRANSIENT_PROPERTY, False)):
                    data_collection.remove(data_block)
            except (ReferenceError, RuntimeError):
                pass


def _runtime_nla_tracks(animation_data):
    if animation_data is None:
        return []
    return [
        track for track in animation_data.nla_tracks
        if str(track.name).startswith(TRANSIENT_NLA_PREFIX)
    ]


def _remove_runtime_nla(animation_data):
    actions = []
    for track in _runtime_nla_tracks(animation_data):
        for strip in track.strips:
            action = getattr(strip, "action", None)
            if action is not None and action not in actions:
                actions.append(action)
        try:
            animation_data.nla_tracks.remove(track)
        except (ReferenceError, RuntimeError):
            pass
    return actions


def _restore_target(target, restore_scene=True):
    if target is None or target.type != "ARMATURE":
        return
    _remove_runtime_visuals(target)
    animation_data = target.animation_data
    transient = animation_data.action if animation_data is not None else None
    if transient is not None and not bool(transient.get(TRANSIENT_PROPERTY, False)):
        transient = None
    layered_actions = _remove_runtime_nla(animation_data)
    captured = _captured_action_for(target)
    if animation_data is None and captured is not None:
        animation_data = target.animation_data_create()
    if animation_data is not None:
        animation_data.action = captured
        settings = _action_settings.pop(target.name, None)
        if settings is not None:
            blend_type, extrapolation, influence = settings
            animation_data.action_blend_type = blend_type
            animation_data.action_extrapolation = extrapolation
            animation_data.action_influence = influence
    if transient is not None:
        _remove_action(transient)
    for action in layered_actions:
        if action != transient:
            _remove_action(action)
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
        has_runtime_tracks = bool(_runtime_nla_tracks(obj.animation_data))
        if (action is not None and bool(action.get(TRANSIENT_PROPERTY, False))) or has_runtime_tracks:
            if restore:
                _restore_target(obj, restore_scene=False)
            else:
                layered_actions = _remove_runtime_nla(obj.animation_data)
                obj.animation_data.action = None
                _remove_action(action)
                for layered_action in layered_actions:
                    if layered_action != action:
                        _remove_action(layered_action)
    for action in list(bpy.data.actions):
        if bool(action.get(TRANSIENT_PROPERTY, False)) and action.users == 0:
            _remove_action(action)


def _data_snapshot():
    names = (
        "objects", "actions", "meshes", "armatures", "materials", "images",
        "textures", "collections", "cameras", "lights", "node_groups",
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


def _set_action_slot(holder, selected, label):
    current = getattr(holder, "action_slot", None)
    selected_handle = getattr(selected, "handle", None)
    if current is not None and getattr(current, "handle", None) == selected_handle:
        return
    try:
        if hasattr(holder, "action_slot_handle") and selected_handle is not None:
            # Blender 5.0/5.2 can crash in RNA when the slot pointer itself is
            # assigned. The integer handle is Blender's safe binding path.
            holder.action_slot_handle = selected_handle
        else:  # Legacy slot API, if a future/older compatible build exposes it.
            holder.action_slot = selected
    except Exception as error:
        raise ClipError(f"{label} could not bind to its armature slot: {error}") from error
    current = getattr(holder, "action_slot", None)
    if current is None or getattr(current, "handle", None) != selected_handle:
        raise ClipError(f"{label} did not bind to its expected armature slot")


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
    _set_action_slot(animation_data, selected, f"Animation Action '{action.name}'")


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
    new_action_settings = target.name not in _action_settings
    if new_action_settings:
        _action_settings[target.name] = (
            animation_data.action_blend_type,
            animation_data.action_extrapolation,
            float(animation_data.action_influence),
        )
    old_action = animation_data.action
    old_action_slot = getattr(animation_data, "action_slot", None)
    if old_action is not None and not bool(old_action.get(TRANSIENT_PROPERTY, False)):
        _captured_actions[target.name] = old_action
    new_scene_session = target.name not in _scene_settings
    try:
        animation_data.action = copied_action
        _bind_action_slot(animation_data, copied_action, source_slot_identifier)
        animation_data.action_blend_type = "REPLACE"
        animation_data.action_extrapolation = "HOLD"
        animation_data.action_influence = 1.0
    except Exception as error:
        try:
            animation_data.action = old_action
            if old_action_slot is not None and hasattr(animation_data, "action_slot"):
                _set_action_slot(animation_data, old_action_slot, "Previous Action")
        except Exception:
            animation_data.action = None
        if new_action_settings:
            blend_type, extrapolation, influence = _action_settings.pop(target.name)
            animation_data.action_blend_type = blend_type
            animation_data.action_extrapolation = extrapolation
            animation_data.action_influence = influence
        _remove_action(copied_action)
        scene.render.fps = previous_scene_settings[2]
        scene.render.fps_base = previous_scene_settings[3]
        raise ClipError(f"The clip Action is incompatible with armature '{target.name}': {error}") from error
    if new_scene_session:
        _scene_settings[target.name] = previous_scene_settings
    old_layered_actions = _remove_runtime_nla(animation_data)
    if old_action is not None and bool(old_action.get(TRANSIENT_PROPERTY, False)):
        _remove_action(old_action)
    for old_layered_action in old_layered_actions:
        if old_layered_action != old_action:
            _remove_action(old_layered_action)
    _remove_runtime_visuals(target)

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


def _import_layer_action(context, clip_path, entry, variant, layer, layer_index):
    """Import one external layer and retain only its copied transient Action."""
    if not clip_path.is_file():
        raise ClipError(f"Animation layer is not ready: {clip_path.name}")
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
        imported_objects = [
            obj for obj in bpy.data.objects
            if obj not in snapshot.get("objects", set())
        ]
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
            new_actions = [
                action for action in bpy.data.actions
                if action not in snapshot.get("actions", set())
            ]
            source_action = new_actions[0] if new_actions else None
        if source_action is None:
            raise ClipError(f"{clip_path.name} contains no animation Action")

        kind = str(_field(layer, "Kind", default="Layer") or "Layer")
        source_name = str(_field(layer, "SourceAnimation", default="") or "").strip()
        copied_action = source_action.copy()
        copied_action.name = (
            f"XivBlend Runtime | {_entry_name(entry)} | {kind} {layer_index + 1}"
            f"{f' | {source_name}' if source_name else ''}"
        )
        copied_action.use_fake_user = False
        copied_action[TRANSIENT_PROPERTY] = True
        copied_action[SOURCE_CLIP_PROPERTY] = str(clip_path)
        copied_action["xivblend_entry_id"] = _id_text(_entry_id(entry))
        copied_action["xivblend_emote_id"] = _id_text(_entry_emote_id(entry))
        copied_action["xivblend_variant_id"] = _id_text(_variant_id(variant))
        copied_action["xivblend_layer_kind"] = kind
    except Exception:
        _cleanup_import(snapshot, keep_action=copied_action)
        if copied_action is not None:
            _remove_action(copied_action)
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
    return copied_action, source_slot_identifier


def _bind_nla_slot(strip, action, source_slot_identifier):
    if not hasattr(strip, "action_slot"):
        return
    slots = list(getattr(action, "slots", ()))
    if not slots:
        return
    selected = next((
        slot for slot in slots
        if slot.identifier == source_slot_identifier
        and getattr(slot, "target_id_type", "OBJECT") == "OBJECT"
    ), None)
    compatible = [
        slot for slot in slots
        if getattr(slot, "target_id_type", "OBJECT") == "OBJECT"
    ]
    if selected is None and len(compatible) == 1:
        selected = compatible[0]
    if selected is None:
        raise ClipError(f"Animation Action '{action.name}' has no unambiguous armature slot")
    _set_action_slot(strip, selected, f"Animation layer '{action.name}'")


def _add_runtime_layer(animation_data, action, source_slot_identifier, layer, layer_index):
    kind = str(_field(layer, "Kind", default="Layer") or "Layer")
    track_order = _int_value(_field(layer, "TrackOrder"), 0)
    item_order = _int_value(_field(layer, "ItemOrder"), layer_index)
    start_frame = _int_value(_field(layer, "StartFrame"), 0)
    duration_frames = max(1, _int_value(_field(layer, "DurationFrames"), 1))
    action_start, action_end = (float(value) for value in action.frame_range)
    source_start = _float_value(_field(layer, "SourceStartFrame"), action_start)
    source_end = _float_value(_field(layer, "SourceEndFrame"), action_end)
    source_span = source_end - source_start
    # A held expression can contain a single sampled frame.  Give the NLA strip
    # a one-frame source window and stretch that constant value over the event;
    # interpolating between fabricated keys would make the face flicker.
    if source_span <= 1.0e-6:
        source_end = source_start + 1.0
        source_span = 1.0

    track = animation_data.nla_tracks.new()
    track.name = (
        f"{TRANSIENT_NLA_PREFIX}{track_order:04d}.{item_order:04d} | "
        f"{kind} {layer_index + 1}"
    )
    try:
        strip = track.strips.new(action.name, start_frame, action)
        strip.action_frame_start = source_start
        strip.action_frame_end = source_end
        strip.repeat = 1.0
        strip.scale = max(float(duration_frames) / source_span, 1.0e-6)
        strip.frame_start = float(start_frame)
        strip.frame_end = float(start_frame + duration_frames)
        strip.extrapolation = "NOTHING"
        strip.blend_type = "REPLACE"
        strip.blend_in = 0.0
        strip.blend_out = 0.0
        strip.use_auto_blend = False
        _bind_nla_slot(strip, action, source_slot_identifier)
    except Exception:
        try:
            animation_data.nla_tracks.remove(track)
        except (ReferenceError, RuntimeError):
            pass
        raise
    return track


def _ordered_bundle_layers(manifest):
    indexed = [
        (index, layer) for index, layer in enumerate(_as_list(_field(manifest, "Layers")))
        if isinstance(layer, dict)
    ]
    return sorted(indexed, key=lambda item: (
        _int_value(_field(item[1], "TrackOrder"), 0),
        _int_value(_field(item[1], "ItemOrder"), item[0]),
        item[0],
    ))


def _runtime_effect_collection(scene, target):
    collection = bpy.data.collections.new(f"{RUNTIME_EFFECT_COLLECTION_PREFIX}{target.name}")
    collection[TRANSIENT_PROPERTY] = True
    collection["xivblend_runtime_target"] = target.name
    scene.collection.children.link(collection)
    return collection


def _key_runtime_visibility(obj, start_frame, duration_frames):
    start = int(start_frame)
    end = start + max(1, int(duration_frames))
    for frame, hidden in ((start - 1, True), (start, False), (end, True)):
        obj.hide_viewport = hidden
        obj.hide_render = hidden
        obj.keyframe_insert(data_path="hide_viewport", frame=frame)
        obj.keyframe_insert(data_path="hide_render", frame=frame)
    action = obj.animation_data.action if obj.animation_data is not None else None
    if action is not None:
        action[TRANSIENT_PROPERTY] = True
        action["xivblend_runtime_effect"] = True


def _is_sync_control_vfx(event):
    path = str(_field(event, "GamePath", default="") or "").replace("\\", "/").casefold()
    return path == SYNC_CONTROL_VFX


def _material_runtime_modules():
    addon_root = Path(__file__).resolve().parent
    package_root = (addon_root / "MeddleTools").resolve()
    # The installer deliberately places the runtime inside the add-on folder.
    # The reviewed source tree keeps both folders next to one another, which is
    # useful for background validation before packaging a release.
    if not (package_root / "shaders.blend").is_file():
        source_tree_runtime = (addon_root.parent / "MeddleTools").resolve()
        if source_tree_runtime.parent == addon_root.parent.resolve():
            package_root = source_tree_runtime
    if not (package_root / "shaders.blend").is_file():
        raise ClipError(
            "The exact FFXIV prop material runtime is missing. Reinstall the XivBlend Blender panel."
        )
    package = sys.modules.get(MATERIAL_RUNTIME_MODULE)
    if package is None:
        package = types.ModuleType(MATERIAL_RUNTIME_MODULE)
        package.__file__ = str(package_root / "__init__.py")
        package.__package__ = MATERIAL_RUNTIME_MODULE
        package.__path__ = [str(package_root)]
        sys.modules[MATERIAL_RUNTIME_MODULE] = package
    version = importlib.import_module(f"{MATERIAL_RUNTIME_MODULE}.version")
    blend_import = importlib.import_module(f"{MATERIAL_RUNTIME_MODULE}.blend_import")
    node_configs = importlib.import_module(
        f"{MATERIAL_RUNTIME_MODULE}.node_setup.node_configs"
    )
    return version, blend_import, node_configs


def _new_data(snapshot, name):
    collection = getattr(bpy.data, name, None)
    if collection is None:
        return []
    return [value for value in collection if value not in snapshot.get(name, set())]


def _tag_transient_data(snapshot):
    for name in (
        "actions", "meshes", "armatures", "materials", "images", "textures",
        "collections", "node_groups",
    ):
        for data_block in _new_data(snapshot, name):
            try:
                data_block[TRANSIENT_PROPERTY] = True
            except (AttributeError, ReferenceError, TypeError):
                pass


def _remove_zero_user_new_data(snapshot):
    # Source glTF materials and unused shader templates become unreferenced
    # after MeddleTools maps the prop. Remove only data created by this import.
    for name in (
        "materials", "images", "textures", "node_groups", "armatures",
        "actions", "meshes",
    ):
        collection = getattr(bpy.data, name, None)
        if collection is None:
            continue
        for data_block in list(_new_data(snapshot, name)):
            try:
                if data_block.users == 0:
                    collection.remove(data_block)
            except (AttributeError, ReferenceError, RuntimeError):
                pass


def _map_runtime_prop_materials(imported_objects, cache_directory, snapshot):
    material_slots = {}
    for obj in imported_objects:
        if obj.type != "MESH":
            continue
        for slot in obj.material_slots:
            if slot.material is not None:
                material_slots.setdefault(slot.material, []).append(slot)
    expected = {
        material for material in material_slots
        if str(material.get("ShaderPackage", "") or "").strip()
    }
    if not expected:
        _tag_transient_data(snapshot)
        return 0

    version, blend_import, node_configs = _material_runtime_modules()
    version.updateCurrentRelease()
    blend_import.import_shaders()
    failures = []
    for source_material, slots in material_slots.items():
        node_configs.map_mesh(source_material, slots, str(cache_directory))
        if source_material not in expected:
            continue
        replacements = [slot.material for slot in slots]
        if not all(
            replacement is not None
            and replacement is not source_material
            and replacement.node_tree is not None
            for replacement in replacements
        ):
            failures.append(
                f"{source_material.name} ({source_material.get('ShaderPackage')})"
            )
    _bind_runtime_optional_texture_fallbacks(imported_objects)
    _tag_transient_data(snapshot)
    _remove_zero_user_new_data(snapshot)
    if failures:
        raise ClipError(
            "The exact FFXIV material mapper could not map " + ", ".join(failures)
        )
    return len(expected)


def _bind_runtime_optional_texture_fallbacks(imported_objects):
    """Keep absent optional decals transparent on temporary game props.

    MeddleTools leaves an Image Texture unassigned when an FFXIV material has
    no decal. Blender evaluates that node's alpha as opaque, so the final decal
    mix selects its black color in both Cycles and some Eevee paths. The
    character builder already supplies this neutral input; runtime props need
    the same treatment because they are mapped only after an emote is clicked.
    """
    materials = {
        slot.material
        for obj in imported_objects
        if obj.type == "MESH"
        for slot in obj.material_slots
        if slot.material is not None and slot.material.node_tree is not None
    }
    missing_decals = [
        node
        for material in materials
        for node in material.node_tree.nodes
        if node.type == "TEX_IMAGE"
        and node.image is None
        and "decal" in str(node.label).casefold()
        and any(output.is_linked for output in node.outputs)
    ]
    if not missing_decals:
        return 0

    fallback = next(
        (
            image
            for image in bpy.data.images
            if image.get("xivblend_component") == "runtime_optional_texture_fallback"
            and image.source == "GENERATED"
            and tuple(image.size) == (1, 1)
        ),
        None,
    )
    if fallback is None:
        fallback = bpy.data.images.new(
            "XivBlend Runtime Transparent Optional Texture",
            width=1,
            height=1,
            alpha=True,
        )
    fallback.generated_color = (0.0, 0.0, 0.0, 0.0)
    try:
        fallback.colorspace_settings.name = "Non-Color"
    except (TypeError, ValueError, RuntimeError):
        pass
    fallback[TRANSIENT_PROPERTY] = True
    fallback["xivblend_component"] = "runtime_optional_texture_fallback"
    for node in missing_decals:
        node.image = fallback
    return len(missing_decals)


def _resolve_prop_asset(catalog, event):
    status = str(_field(event, "AssetStatus", default="") or "").strip()
    relative = str(_field(event, "AssetRelativePath", default="") or "").strip()
    if status and status.casefold() not in {"ready", "supported", "available"}:
        raise ClipError(status)
    if not relative:
        raise ClipError("no real extracted prop asset is available; rebuild this emote bundle")
    try:
        asset_path = _resolve_child(catalog["library_root"], relative, "AssetRelativePath")
    except CatalogError as error:
        raise ClipError(str(error)) from error
    if asset_path.suffix.casefold() not in {".glb", ".gltf"}:
        raise ClipError(f"prop asset must be glTF: {asset_path.name}")
    if not _valid_animation_layer_file(asset_path):
        raise ClipError(f"real prop asset is missing or invalid: {asset_path.name}")

    cache_relative = str(
        _field(event, "AssetCacheRelativePath", default="") or ""
    ).strip()
    if not cache_relative:
        cache_path = asset_path.parent / "cache"
    else:
        try:
            cache_path = _resolve_child(
                catalog["library_root"], cache_relative, "AssetCacheRelativePath"
            )
        except CatalogError as error:
            raise ClipError(str(error)) from error
    if not _inside(cache_path, catalog["default_root"]):
        raise ClipError("prop material cache is outside XivBlend/AnimationLibrary")
    if not cache_path.is_dir():
        raise ClipError("real prop material cache is missing; rebuild this emote bundle")
    return asset_path, cache_path


def _attachment_bone(target, event):
    configured = str(_field(event, "AttachmentBone", default="") or "").strip()
    flags = _int_value(_field(event, "Flags", "AttachmentFlags"), 0)
    if not configured:
        configured = "n_buki_l" if flags & 0x1 else "n_buki_r"
    fallbacks = (
        configured,
        "j_te_l" if configured.endswith("_l") else "j_te_r",
    )
    return next((name for name in fallbacks if target.pose.bones.get(name) is not None), None)


def _bone_axis_correction(target, bone_name):
    bone = target.data.bones.get(bone_name) if target is not None else None
    raw = bone.get(GLTF_BONE_AXIS_CORRECTION_PROPERTY) if bone is not None else None
    if raw is not None:
        try:
            values = [float(value) for value in raw]
        except (TypeError, ValueError, OverflowError) as error:
            raise ClipError(f"attachment bone '{bone_name}' has invalid axis metadata") from error
        if len(values) != 16 or not all(math.isfinite(value) for value in values):
            raise ClipError(f"attachment bone '{bone_name}' has invalid axis metadata")
        correction = Matrix(
            (
                values[0:4],
                values[4:8],
                values[8:12],
                values[12:16],
            )
        )
        if abs(correction.determinant()) < 1.0e-8:
            raise ClipError(f"attachment bone '{bone_name}' has singular axis metadata")
        return correction

    # Browser 0.5 and older XivBlend files predate preserved glTF joint axes.
    # Blender's default importer rotates these known XIV socket/display bones
    # +90 degrees around local X, so undo that only on a tagged XivBlend rig.
    tagged_xivblend_rig = _id_property(
        target,
        "xivblend_race_code",
        "XivBlendRaceCode",
    ) is not None or _id_property(
        getattr(target, "data", None),
        "xivblend_race_code",
        "XivBlendRaceCode",
    ) is not None
    if tagged_xivblend_rig and bone_name in LEGACY_GLTF_SOCKET_BONES:
        return Euler((-math.pi / 2.0, 0.0, 0.0), "XYZ").to_matrix().to_4x4()
    return Matrix.Identity(4)


def _attachment_matrix(event, target=None, bone_name=""):
    scale = _float_value(_field(event, "AttachmentScale", "Scale"), 1.0)
    if not math.isfinite(scale) or scale <= 0.0 or scale > 100.0:
        scale = 1.0
    offset = Vector((
        _float_value(_field(event, "AttachmentOffsetX", "OffsetX"), 0.0),
        _float_value(_field(event, "AttachmentOffsetY", "OffsetY"), 0.0),
        _float_value(_field(event, "AttachmentOffsetZ", "OffsetZ"), 0.0),
    ))
    rotation = Euler((
        _float_value(_field(event, "AttachmentRotationX", "RotationX"), 0.0),
        _float_value(_field(event, "AttachmentRotationY", "RotationY"), 0.0),
        _float_value(_field(event, "AttachmentRotationZ", "RotationZ"), 0.0),
    ), "XYZ").to_matrix().to_4x4()
    # SharpGLTF stores the FFXIV Y-up transform. Blender's glTF importer maps
    # it to X/Z-up as (x, -z, y); apply the same basis to ATCH metadata.
    basis = Matrix((
        (1.0, 0.0, 0.0, 0.0),
        (0.0, 0.0, -1.0, 0.0),
        (0.0, 1.0, 0.0, 0.0),
        (0.0, 0.0, 0.0, 1.0),
    ))
    game_matrix = Matrix.Translation(offset) @ rotation @ Matrix.Scale(scale, 4)
    attachment = basis @ game_matrix @ basis.inverted()
    return _bone_axis_correction(target, bone_name) @ attachment


def _restore_selection(context, original_active, original_selected):
    try:
        bpy.ops.object.select_all(action="DESELECT")
        for obj in original_selected:
            if obj.name in bpy.data.objects:
                obj.select_set(True)
        if original_active is not None and original_active.name in bpy.data.objects:
            context.view_layer.objects.active = original_active
    except Exception:
        pass


def _import_runtime_prop(context, collection, target, catalog, event, index):
    asset_path, cache_path = _resolve_prop_asset(catalog, event)
    bone_name = _attachment_bone(target, event)
    if bone_name is None:
        raise ClipError("the exported rig has no compatible attachment bone")

    snapshot = _data_snapshot()
    original_active = getattr(context.view_layer.objects, "active", None)
    original_selected = list(getattr(context, "selected_objects", []))
    imported_objects = []
    try:
        _set_object_mode(context)
        result = bpy.ops.import_scene.gltf(
            filepath=str(asset_path), disable_bone_shape=True
        )
        if "FINISHED" not in result:
            raise ClipError(f"Blender could not import {asset_path.name}")
        imported_objects = _new_data(snapshot, "objects")
        if not imported_objects or not any(obj.type == "MESH" for obj in imported_objects):
            raise ClipError(f"{asset_path.name} contains no prop mesh")
        if any(obj.type == "ARMATURE" for obj in imported_objects):
            raise ClipError(f"{asset_path.name} is not a safe rigid prop asset")

        mapped_count = _map_runtime_prop_materials(
            imported_objects, cache_path, snapshot
        )
        imported_set = set(imported_objects)
        roots = [obj for obj in imported_objects if obj.parent not in imported_set]
        model_id = _int_value(_field(event, "ModelId"), 0)
        body_id = _int_value(_field(event, "BodyId"), 0)
        variant = _int_value(_field(event, "Variant"), 0)
        bone_anchor = bpy.data.objects.new(
            f"XivBlend Prop Bone | {bone_name} | {index + 1}",
            None,
        )
        bone_anchor[TRANSIENT_PROPERTY] = True
        bone_anchor["xivblend_runtime_real_game_asset"] = True
        bone_anchor["xivblend_attachment_bone"] = bone_name
        bone_anchor.empty_display_type = "PLAIN_AXES"
        bone_anchor.empty_display_size = 0.02
        collection.objects.link(bone_anchor)
        constraint = bone_anchor.constraints.new(type="COPY_TRANSFORMS")
        constraint.name = "XivBlend Exact ATCH Bone"
        constraint.target = target
        constraint.subtarget = bone_name

        anchor = bpy.data.objects.new(
            f"XivBlend Prop ATCH | w{model_id:04d}b{body_id:04d}v{variant:04d} | {index + 1}",
            None,
        )
        anchor[TRANSIENT_PROPERTY] = True
        anchor["xivblend_runtime_real_game_asset"] = True
        anchor["xivblend_attachment_bone"] = bone_name
        anchor["xivblend_model_game_path"] = str(
            _field(event, "ModelGamePath", default="") or ""
        )
        anchor.empty_display_type = "PLAIN_AXES"
        anchor.empty_display_size = 0.025
        collection.objects.link(anchor)
        anchor.parent = bone_anchor
        anchor.matrix_parent_inverse = Matrix.Identity(4)
        anchor.matrix_basis = _attachment_matrix(event, target, bone_name)

        for obj in imported_objects:
            obj[TRANSIENT_PROPERTY] = True
            obj["xivblend_runtime_real_game_asset"] = True
            for owner in list(obj.users_collection):
                try:
                    owner.objects.unlink(obj)
                except RuntimeError:
                    pass
            if obj.name not in collection.objects:
                collection.objects.link(obj)
        for root in roots:
            local_matrix = root.matrix_world.copy()
            root.parent = anchor
            root.matrix_parent_inverse = Matrix.Identity(4)
            root.matrix_basis = local_matrix

        start = _int_value(_field(event, "StartFrame"), 0)
        duration = max(1, _int_value(_field(event, "DurationFrames"), 1))
        for obj in imported_objects:
            _key_runtime_visibility(obj, start, duration)

        # The glTF importer may create a temporary scene collection. All kept
        # objects now live in XivBlend's transient runtime collection.
        for imported_collection in list(_new_data(snapshot, "collections")):
            if imported_collection == collection:
                continue
            try:
                if len(imported_collection.objects) == 0:
                    bpy.data.collections.remove(imported_collection)
            except (ReferenceError, RuntimeError):
                pass
        _tag_transient_data(snapshot)
        event["_RuntimeStatus"] = "Loaded exact game prop"
        event["_RuntimeBone"] = bone_name
        event["_RuntimeObjectCount"] = len(imported_objects)
        event["_RuntimeMappedMaterials"] = mapped_count
        return len(imported_objects)
    except Exception:
        _cleanup_import(snapshot)
        raise
    finally:
        _restore_selection(context, original_active, original_selected)


def _create_bundle_visuals(context, target, manifest, catalog):
    props = [
        event for event in _as_list(_field(manifest, "Props"))
        if isinstance(event, dict)
    ]
    if not props:
        return []
    collection = _runtime_effect_collection(context.scene, target)
    warnings = []
    loaded = 0
    for index, event in enumerate(props):
        try:
            loaded += _import_runtime_prop(
                context, collection, target, catalog, event, index
            )
        except Exception as error:
            event["_RuntimeStatus"] = f"Not loaded: {error}"
            warnings.append(
                f"Prop w{_int_value(_field(event, 'ModelId'), 0):04d}/"
                f"b{_int_value(_field(event, 'BodyId'), 0):04d} was not loaded: {error}"
            )
    if loaded == 0:
        try:
            bpy.data.collections.remove(collection)
        except (ReferenceError, RuntimeError):
            pass
    return warnings


def _bundle_runtime_warnings(manifest):
    warnings = [
        str(value).strip()
        for value in _as_list(_field(manifest, "Warnings"))
        if str(value).strip()
    ]
    effects = [
        value for value in _as_list(_field(manifest, "VisualEffects"))
        if isinstance(value, dict)
    ]
    native_effects = [value for value in effects if not _is_sync_control_vfx(value)]
    cached_effects = [
        value for value in native_effects
        if str(_field(value, "SourceRelativePath", default="") or "").strip()
    ]
    failed_count = len(native_effects) - len(cached_effects)
    if cached_effects:
        warnings.append(
            f"{len(cached_effects)} exact native AVFX source event(s) are cached with game timing and placement; "
            "Blender does not simulate XIV's Apricot particle runtime yet"
        )
    if failed_count:
        warnings.append(f"{failed_count} native AVFX source event(s) could not be extracted")
    return warnings


def _import_bundle(context, target, bundle_path, catalog, entry, variant, auto_play=True):
    manifest = _read_bundle_manifest(catalog, bundle_path)
    _validate_bundle_identity(manifest, catalog, entry, variant)
    ordered_layers = _ordered_bundle_layers(manifest)
    if not ordered_layers:
        raise ClipError(f"Animation bundle {Path(bundle_path).name} contains no playable layers")
    primary = next((item for item in ordered_layers if str(
        _field(item[1], "Kind", default="") or ""
    ).casefold() == "body"), ordered_layers[0])
    primary_index, primary_layer = primary
    primary_path = _bundle_layer_path(catalog, primary_layer)
    if not primary_path.is_file():
        raise ClipError(f"Animation bundle layer is not ready: {primary_path.name}")

    created_actions = []
    layered_actions = {}
    try:
        primary_action = _import_clip(
            context, target, primary_path, entry, variant, auto_play=False
        )
        created_actions.append(primary_action)
        animation_data = target.animation_data_create()
        # Blender evaluates the active Action after its NLA stack. COMBINE lets
        # timed facial strips contribute on facial bones even when the body GLB
        # contains harmless identity channels for those bones.
        if len(ordered_layers) > 1:
            animation_data.action_blend_type = "COMBINE"
        for layer_index, layer in ordered_layers:
            if layer_index == primary_index:
                continue
            layer_path = _bundle_layer_path(catalog, layer)
            layer_key = os.path.normcase(str(layer_path.resolve()))
            if layer_key not in layered_actions:
                action, slot_identifier = _import_layer_action(
                    context, layer_path, entry, variant, layer, layer_index
                )
                layered_actions[layer_key] = (action, slot_identifier)
                created_actions.append(action)
            else:
                action, slot_identifier = layered_actions[layer_key]
            _add_runtime_layer(
                animation_data, action, slot_identifier, layer, layer_index
            )
    except Exception:
        _restore_target(target, restore_scene=True)
        for action in created_actions:
            _remove_action(action)
        raise

    try:
        visual_warnings = _create_bundle_visuals(context, target, manifest, catalog)
    except Exception as error:
        _remove_runtime_visuals(target)
        visual_warnings = [f"Real runtime prop import failed: {error}"]

    scene = context.scene
    fps = max(1, min(240, _int_value(_field(manifest, "FramesPerSecond"), 30)))
    frame_start = _int_value(_field(manifest, "FrameStart"), int(math.floor(primary_action.frame_range[0])))
    frame_end = _int_value(_field(manifest, "FrameEnd"), int(math.ceil(primary_action.frame_range[1])))
    frame_end = max(frame_start + 1, frame_end)
    scene.render.fps = fps
    scene.render.fps_base = 1.0
    scene.use_preview_range = False
    scene.frame_start = frame_start
    scene.frame_end = frame_end
    scene.frame_set(frame_start)
    warnings = _bundle_runtime_warnings(manifest) + visual_warnings
    _runtime_sessions[target.name] = {
        "target": target.name,
        "asset_kind": "bundle",
        "bundle_path": str(Path(bundle_path).resolve()),
        "clip_path": str(primary_path),
        "entry": entry,
        "variant": variant,
        "manifest": manifest,
        "frame": frame_start,
        "playing": bool(auto_play),
        "warnings": warnings,
    }
    if auto_play:
        _start_playback()
    return primary_action, manifest, warnings


def _import_animation_asset(context, target, asset_kind, asset_path, catalog, entry, variant, auto_play=True):
    if asset_kind == "bundle" or Path(asset_path).suffix.casefold() == ".json":
        action, manifest, warnings = _import_bundle(
            context, target, asset_path, catalog, entry, variant, auto_play=auto_play
        )
        return action, manifest, warnings
    action = _import_clip(context, target, asset_path, entry, variant, auto_play=auto_play)
    return action, None, []


def _request_paths(library_root, request_id):
    return (
        library_root / "responses" / f"{request_id}.json",
        library_root / "requests" / f"{request_id}.response.json",
        library_root / "requests" / f"{request_id}.result.json",
    )


def _atomic_request(catalog, entry, variant, race, face, expected_kind, expected_path, target):
    request_id = str(uuid.uuid4())
    request_folder = (catalog["library_root"] / "requests").resolve()
    if not _inside(request_folder, catalog["default_root"]):
        raise ClipError("Animation request folder is outside XivBlend/AnimationLibrary")
    request_folder.mkdir(parents=True, exist_ok=True)
    destination = request_folder / f"{request_id}.json"
    temporary = request_folder / f".{request_id}.{uuid.uuid4().hex}.tmp"
    request_schema = 2 if catalog.get("schema_version", 1) >= 2 or _field(entry, "EntryId") else 1
    payload = {
        "schemaVersion": request_schema,
        "requestId": request_id,
        "variantId": _variant_id(variant),
        # The cache path uses FFXIV's c0801 notation, while the C# request
        # contract intentionally serializes RaceCode as an unsigned number.
        "raceCode": int(race[1:]),
        "faceSkeleton": face,
        "gameVersion": catalog["game_version"],
    }
    if request_schema >= 2:
        payload["entryId"] = _id_text(_entry_id(entry))
        emote_id = _entry_emote_id(entry)
        if emote_id is not None:
            payload["emoteId"] = emote_id
    else:
        payload["emoteId"] = _entry_emote_id(entry)
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
        "expected_kind": expected_kind,
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
            for field_name, kind, suffixes in (
                ("BundleRelativePath", "bundle", {".json"}),
                ("ClipRelativePath", "clip", {".glb", ".gltf"}),
            ):
                relative = _field(response, field_name)
                if not relative:
                    continue
                try:
                    asset_path = _resolve_child(
                        Path(record["library_root"]), relative, field_name
                    )
                except CatalogError as error:
                    return str(error), None
                if asset_path.suffix.casefold() not in suffixes:
                    return f"The animation response has an invalid {field_name}", None
                return None, {
                    "kind": kind,
                    "path": asset_path,
                    "warnings": [
                        str(value).strip() for value in _as_list(_field(response, "Warnings"))
                        if str(value).strip()
                    ],
                }
            return "The completed animation response contains no clip or bundle path", None
    return None, None


def _playing_status(entry, warnings=None):
    source = _entry_source_description(entry)
    base = f"Playing {_entry_name(entry)} on a loop"
    if _entry_source_badge(entry):
        base += f" • {source}"
    clean_warnings = list(dict.fromkeys(
        str(value).strip() for value in (warnings or []) if str(value).strip()
    ))
    if clean_warnings:
        base += f" • Warning: {clean_warnings[0]}"
        if len(clean_warnings) > 1:
            base += f" (+{len(clean_warnings) - 1} more)"
    return base


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
        error, response_asset = _response_state(record)
        if error:
            _pending_requests.pop(request_id, None)
            _cleanup_response_files(record)
            _set_status(error)
            continue

        asset_kind = response_asset["kind"] if response_asset else record.get("expected_kind", "clip")
        asset_path = response_asset["path"] if response_asset else Path(record["expected_path"])
        response_warnings = response_asset.get("warnings", []) if response_asset else []
        catalog, catalog_error = _catalog_or_error(force=False)
        if catalog is None:
            _pending_requests.pop(request_id, None)
            _cleanup_response_files(record)
            _set_status(catalog_error or "The animation catalog could not be reloaded")
            continue
        if not asset_path.is_file():
            # A catalog replacement can publish a changed deterministic path.
            entry = _find_entry(catalog, record["entry_id"])
            variant = _find_variant(entry, record["variant_id"]) if entry is not None else None
            target = bpy.data.objects.get(record["target"])
            if variant is not None and target is not None:
                try:
                    kind = str(_field(variant, "Kind", default="Body") or "Body")
                    race, face = _rig_identity(
                        bpy.context, target, require_face=kind.casefold() == "face"
                    )
                    asset_kind, asset_path = _expected_animation_asset(
                        catalog, entry, variant, race, face
                    )
                    record["expected_kind"] = asset_kind
                    record["expected_path"] = str(asset_path)
                    record["entry"] = entry
                    record["variant"] = variant
                except Exception:
                    pass
        if not asset_path.is_file():
            continue

        target = bpy.data.objects.get(record["target"])
        if target is None or target.type != "ARMATURE":
            _pending_requests.pop(request_id, None)
            _cleanup_response_files(record)
            _set_status("The target armature was removed before the animation was ready")
            continue
        try:
            _, _, bundle_warnings = _import_animation_asset(
                bpy.context, target, asset_kind, asset_path, catalog,
                record["entry"], record["variant"], auto_play=True,
            )
        except Exception as error:
            record["import_failures"] += 1
            if record["import_failures"] >= 3:
                _pending_requests.pop(request_id, None)
                _cleanup_response_files(record)
                _set_status(f"Could not import {_entry_name(record['entry'])}: {error}")
            continue
        _pending_requests.pop(request_id, None)
        _cleanup_response_files(record)
        _set_status(_playing_status(record["entry"], response_warnings + bundle_warnings))
    return POLL_SECONDS if _pending_requests else None


def _ensure_poll_timer():
    try:
        if not bpy.app.timers.is_registered(_poll_requests):
            bpy.app.timers.register(_poll_requests, first_interval=POLL_SECONDS, persistent=True)
    except Exception:
        bpy.app.timers.register(_poll_requests, first_interval=POLL_SECONDS, persistent=True)


def _play(context, entry_id, variant_id=""):
    catalog = _load_catalog()
    entry = _find_entry(catalog, entry_id)
    if entry is None:
        raise ClipError(f"Animation {entry_id} is no longer in the animation catalog")
    variant = _find_variant(entry, variant_id)
    if variant is None:
        raise ClipError(f"'{_entry_name(entry)}' has no playable variant")
    kind = str(_field(variant, "Kind", default="Body") or "Body")
    target = _target_armature(context, kind)
    if target is None:
        raise ClipError("No armature was found. Open an XivBlend character file first.")
    race, face = _rig_identity(context, target, require_face=kind.casefold() == "face")
    variant = _find_variant(entry, variant_id, race)
    if variant is None:
        raise ClipError(
            f"'{_entry_name(entry)}' is not compatible with this character's {race} rig."
        )
    asset_kind, asset_path = _expected_animation_asset(catalog, entry, variant, race, face)
    asset_ready = asset_path.is_file() and (
        asset_kind != "bundle" or _bundle_is_ready(catalog, asset_path, entry, variant)
    )
    if asset_ready:
        _, _, warnings = _import_animation_asset(
            context, target, asset_kind, asset_path, catalog, entry, variant, auto_play=True
        )
        return _playing_status(entry, warnings)

    duplicate = next((
        request_id for request_id, record in _pending_requests.items()
        if record["expected_path"] == str(asset_path) and record["target"] == target.name
    ), None)
    if duplicate:
        return f"Waiting for {_entry_name(entry)} (request {duplicate[:8]})"
    request_id = _atomic_request(
        catalog, entry, variant, race, face, asset_kind, asset_path, target
    )
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


def _set_render_status(message):
    global _render_status
    _render_status = str(message)
    window_manager = getattr(bpy.context, "window_manager", None)
    if window_manager is not None:
        try:
            window_manager.xivblend_render_status = _render_status
        except Exception:
            pass
    _redraw()


def _is_playback_running():
    window_manager = getattr(bpy.context, "window_manager", None)
    if window_manager is None:
        return False
    return any(
        window.screen is not None and getattr(window.screen, "is_animation_playing", False)
        for window in window_manager.windows
    )


def _collection_contains_object(collection, obj):
    try:
        return obj.name in collection.all_objects
    except (AttributeError, ReferenceError, RuntimeError):
        return False


def _object_in_named_collection(obj, collection_name):
    return any(
        collection.name == collection_name and _collection_contains_object(collection, obj)
        for collection in bpy.data.collections
    )


def _custom_shape_objects():
    result = set()
    for armature in bpy.data.objects:
        if armature.type != "ARMATURE" or armature.pose is None:
            continue
        for pose_bone in armature.pose.bones:
            shape = pose_bone.custom_shape
            if shape is not None:
                result.add(shape)
    return result


def _object_hidden(context, obj):
    if obj.hide_render or obj.hide_viewport:
        return True
    try:
        if obj.hide_get(view_layer=context.view_layer):
            return True
    except (AttributeError, RuntimeError, TypeError):
        try:
            if obj.hide_get():
                return True
        except (AttributeError, RuntimeError):
            pass
    try:
        if not obj.visible_get(view_layer=context.view_layer):
            return True
    except (AttributeError, RuntimeError, TypeError):
        pass
    # A Blender object can be linked through more than one collection. It is
    # still visible when at least one collection path is visible, so a hidden
    # secondary collection must not exclude it from character framing.
    collections = list(obj.users_collection)
    return bool(collections) and all(
        collection.hide_render or collection.hide_viewport
        for collection in collections
    )


def _mesh_uses_armature(obj, target):
    if target is None:
        return False
    parent = obj.parent
    while parent is not None:
        if parent == target:
            return True
        parent = parent.parent
    try:
        if obj.find_armature() == target:
            return True
    except (AttributeError, ReferenceError, RuntimeError):
        pass
    return any(
        modifier.type == "ARMATURE" and modifier.object == target
        for modifier in obj.modifiers
    )


def _character_meshes(context, target):
    """Return visible character meshes, never render-rig or studio helpers."""
    custom_shapes = _custom_shape_objects()
    candidates = []
    associated = []
    for obj in context.scene.objects:
        if obj.type != "MESH" or obj in custom_shapes:
            continue
        if bool(obj.get(CUSTOM_BONE_SHAPE_PROPERTY, False)):
            continue
        if str(obj.get(STUDIO_COMPONENT_PROPERTY, "")).startswith("studio_"):
            continue
        if _object_in_named_collection(obj, SCENE_SETUP_COLLECTION):
            continue
        if _object_hidden(context, obj):
            continue
        try:
            if len(obj.data.polygons) == 0 or not obj.bound_box:
                continue
        except (AttributeError, ReferenceError):
            continue
        candidates.append(obj)
        component = str(obj.get("clean_extract_component", ""))
        if (
            component == CHARACTER_MESH_COMPONENT
            or _mesh_uses_armature(obj, target)
            or _object_in_named_collection(obj, CHARACTER_COLLECTION)
        ):
            associated.append(obj)

    # Older XivBlend exports did not have all of today's organization tags. A
    # conservative fallback still makes their portrait camera usable while the
    # studio setup and hidden helpers remain excluded above.
    return associated if associated else candidates


def _evaluated_bound_points(context, meshes):
    context.view_layer.update()
    depsgraph = context.evaluated_depsgraph_get()
    depsgraph.update()
    points = []
    for obj in meshes:
        try:
            evaluated = obj.evaluated_get(depsgraph)
            matrix = evaluated.matrix_world
            for corner in evaluated.bound_box:
                point = matrix @ Vector(corner)
                if all(math.isfinite(float(value)) for value in point):
                    points.append(point)
        except (AttributeError, ReferenceError, RuntimeError, ValueError):
            continue
    if not points:
        raise ClipError("No visible evaluated character geometry was found to frame")
    return points


def _set_fractional_frame(scene, value):
    frame = math.floor(float(value))
    scene.frame_set(frame, subframe=float(value) - frame)


def _points_across_frames(context, meshes, frames):
    scene = context.scene
    original_frame = int(scene.frame_current)
    original_subframe = float(scene.frame_subframe)
    points = []
    try:
        for frame in frames:
            _set_fractional_frame(scene, frame)
            points.extend(_evaluated_bound_points(context, meshes))
    finally:
        scene.frame_set(original_frame, subframe=original_subframe)
        context.view_layer.update()
    return points


def _sample_action_frames(action, current_frame):
    try:
        start, end = (float(value) for value in action.frame_range)
    except (AttributeError, TypeError, ValueError) as error:
        raise ClipError(f"Action '{action.name}' has no usable frame range") from error
    if not (math.isfinite(start) and math.isfinite(end)):
        raise ClipError(f"Action '{action.name}' has an invalid frame range")
    if end < start:
        start, end = end, start
    span = end - start
    if span <= 1.0e-6:
        return [start]
    count = min(MAX_CAMERA_ACTION_SAMPLES, max(2, int(math.ceil(span)) + 1))
    frames = [start + span * index / (count - 1) for index in range(count)]
    if start <= current_frame <= end:
        frames.append(float(current_frame))
    return sorted({round(frame, 6) for frame in frames})


def _camera_is_safe_to_fit(camera):
    return (
        camera is not None
        and camera.type == "CAMERA"
        and camera.data is not None
        and camera.data.type == "PERSP"
        and len(camera.constraints) == 0
    )


def _render_camera(scene):
    current = scene.camera
    if _camera_is_safe_to_fit(current):
        return current
    tagged = sorted(
        (
            obj for obj in scene.objects
            if obj.type == "CAMERA"
            and obj.get(STUDIO_COMPONENT_PROPERTY) == STUDIO_CAMERA_COMPONENT
            and _camera_is_safe_to_fit(obj)
        ),
        key=lambda obj: obj.name.casefold(),
    )
    if tagged:
        return tagged[0]
    if current is not None and current.type == "CAMERA":
        if current.data.type != "PERSP":
            raise ClipError("The active camera is not perspective, and no XivBlend studio camera was found")
        if current.constraints:
            raise ClipError("The active camera is constrained, and no free XivBlend studio camera was found")
    raise ClipError("No portrait camera was found in this scene")


def _camera_frame_slopes(scene, camera):
    try:
        frame = camera.data.view_frame(scene=scene)
        projected = [
            (float(corner.x) / -float(corner.z), float(corner.y) / -float(corner.z))
            for corner in frame
            if -float(corner.z) > 1.0e-8
        ]
    except (AttributeError, RuntimeError, TypeError, ZeroDivisionError) as error:
        raise ClipError(f"Could not read camera '{camera.name}' framing: {error}") from error
    if not projected:
        raise ClipError(f"Camera '{camera.name}' has no usable perspective frame")
    left = min(point[0] for point in projected)
    right = max(point[0] for point in projected)
    bottom = min(point[1] for point in projected)
    top = max(point[1] for point in projected)
    if not (left < 0.0 < right and bottom < 0.0 < top):
        raise ClipError(f"Camera '{camera.name}' has an unsupported lens shift")
    return max(min(-left, right), 1.0e-4), max(min(-bottom, top), 1.0e-4)


def _fit_camera_to_points(scene, camera, points, margin=1.12):
    if not points:
        raise ClipError("No character bounds were supplied to the camera fitter")
    minimum = Vector(tuple(min(point[axis] for point in points) for axis in range(3)))
    maximum = Vector(tuple(max(point[axis] for point in points) for axis in range(3)))
    size = maximum - minimum
    target = (minimum + maximum) * 0.5 + Vector((0.0, 0.0, size.z * 0.02))
    tan_half_x, tan_half_y = _camera_frame_slopes(scene, camera)
    distance = max(
        max(
            abs(point.x - target.x) * margin / tan_half_x - (point.y - target.y),
            abs(point.z - target.z) * margin / tan_half_y - (point.y - target.y),
        )
        for point in points
    )
    extent = max(max(size), 0.1)
    distance = max(distance, extent * 0.75, 0.1)
    position = target + Vector((0.0, -distance, 0.0))
    rotation = (target - position).to_track_quat("-Z", "Y")
    scale = camera.matrix_world.to_scale()
    camera.matrix_world = Matrix.LocRotScale(position, rotation, scale)

    depths = [distance + (point.y - target.y) for point in points]
    closest = max(min(depths), 1.0e-3)
    farthest = max(depths)
    camera.data.clip_start = max(min(closest * 0.05, distance / 100.0), 0.001)
    camera.data.clip_end = max(farthest + extent * 10.0, 100.0)
    scene.camera = camera
    return distance


def _fit_character_camera(context, sample_action):
    target = _target_armature(context)
    if target is None:
        raise ClipError("No character armature was found in this scene")
    camera = _render_camera(context.scene)
    meshes = _character_meshes(context, target)
    if not meshes:
        raise ClipError("No visible character meshes were found to frame")

    if not sample_action:
        points = _evaluated_bound_points(context, meshes)
        sample_count = 1
        action_name = "current pose"
    else:
        animation_data = target.animation_data
        action = animation_data.action if animation_data is not None else None
        if action is None:
            raise ClipError(f"Armature '{target.name}' has no active Action")
        current_frame = float(context.scene.frame_current) + float(context.scene.frame_subframe)
        frames = _sample_action_frames(action, current_frame)
        points = _points_across_frames(context, meshes, frames)
        sample_count = len(frames)
        action_name = action.name

    lens = float(camera.data.lens)
    _fit_camera_to_points(context.scene, camera, points)
    # Camera fitting changes only world transform and clipping distances. Keep
    # the portrait lens exactly as the artist/exporter configured it.
    camera.data.lens = lens
    return camera, action_name, sample_count


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
        entries = _filtered_entries(context, catalog)
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

    entry_id: StringProperty(options={"SKIP_SAVE"})
    # Kept for buttons stored by older add-on builds.
    emote_id: StringProperty(options={"SKIP_SAVE"})
    variant_id: StringProperty(options={"SKIP_SAVE"})

    @classmethod
    def description(cls, _context, properties):
        catalog, _ = _catalog_or_error()
        requested_id = properties.entry_id or properties.emote_id
        entry = _find_entry(catalog, requested_id) if catalog else None
        if entry is None:
            return "Play this emote"
        command = _entry_command(entry)
        source = _entry_source_description(entry)
        return (
            f"Play {_entry_name(entry)}{f' ({command})' if command else ''} on a loop. "
            f"Source: {source}"
        )

    def execute(self, context):
        try:
            message = _play(context, self.entry_id or self.emote_id, self.variant_id)
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


def _set_if_present(owner, name, value):
    if owner is not None and hasattr(owner, name):
        try:
            setattr(owner, name, value)
            return True
        except (AttributeError, TypeError, ValueError):
            pass
    return False


def _clear_legacy_preview_overrides():
    """Remove only XivBlend's retired clay override from older files/sessions."""
    cleared = 0
    # Blender deliberately exposes ``bpy.data`` as _RestrictData while an
    # add-on is being enabled during startup. Registration must not inspect the
    # open file in that phase; load_post will perform the migration afterwards.
    scenes = getattr(bpy.data, "scenes", ())
    materials = getattr(bpy.data, "materials", None)
    for scene in scenes:
        for view_layer in scene.view_layers:
            material = view_layer.material_override
            if material is None or not (
                bool(material.get(LEGACY_PREVIEW_MATERIAL_PROPERTY, False))
                or material.name.startswith(LEGACY_PREVIEW_MATERIAL_NAME)
            ):
                continue
            view_layer.material_override = None
            cleared += 1
    if materials is None:
        return cleared
    for material in list(materials):
        if material.users != 0 or not (
            bool(material.get(LEGACY_PREVIEW_MATERIAL_PROPERTY, False))
            or material.name.startswith(LEGACY_PREVIEW_MATERIAL_NAME)
        ):
            continue
        try:
            materials.remove(material)
        except (ReferenceError, RuntimeError):
            pass
    return cleared


def _view_3d_spaces():
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type != "VIEW_3D":
                continue
            for space in area.spaces:
                if space.type == "VIEW_3D":
                    yield space


def _set_viewport_mode(mode):
    spaces = list(_view_3d_spaces())
    for space in spaces:
        shading = space.shading
        if mode == "ANIMATE":
            shading.type = "SOLID"
            _set_if_present(shading, "light", "STUDIO")
            _set_if_present(shading, "color_type", "MATERIAL")
            _set_if_present(shading, "show_shadows", True)
            _set_if_present(shading, "show_cavity", True)
            _set_if_present(shading, "cavity_type", "BOTH")
            _set_if_present(shading, "curvature_ridge_factor", 0.65)
            _set_if_present(shading, "curvature_valley_factor", 0.45)
            _set_if_present(shading, "show_specular_highlight", True)
            _set_if_present(shading, "background_type", "THEME")
        else:
            shading.type = "RENDERED"
            _set_if_present(shading, "use_scene_lights", True)
            _set_if_present(shading, "use_scene_world", True)
            _set_if_present(shading, "render_pass", "COMBINED")
    return len(spaces)


def _studio_lights(scene):
    return [
        obj
        for obj in scene.objects
        if obj.type == "LIGHT"
        and (
            obj.get(STUDIO_COMPONENT_PROPERTY) == STUDIO_LIGHT_COMPONENT
            or str(obj.name).startswith(("Key Light", "Fill Light", "Rim Light"))
        )
    ]


def _studio_backdrops(scene):
    return [
        obj
        for obj in scene.objects
        if obj.type == "MESH"
        and (
            obj.get(STUDIO_COMPONENT_PROPERTY) == STUDIO_BACKDROP_COMPONENT
            or obj.name == "XivBlend Studio Backdrop"
        )
    ]


def _set_studio_shadow_quality(scene, beauty):
    for light in _studio_lights(scene):
        role = str(light.get("xivblend_studio_role", ""))
        light.data.use_shadow = role == "key" or bool(beauty)


def _cycles_device(scene):
    """Use the user's configured Cycles GPU without changing global preferences."""
    addon = bpy.context.preferences.addons.get("cycles")
    if addon is None:
        scene.cycles.device = "CPU"
        return "Cycles CPU (Cycles preferences unavailable)"
    preferences = addon.preferences
    try:
        preferences.get_devices()
    except Exception:
        pass
    backend = str(getattr(preferences, "compute_device_type", "NONE") or "NONE")
    devices = [
        device
        for device in getattr(preferences, "devices", ())
        if bool(getattr(device, "use", False))
        and str(getattr(device, "type", "CPU")) != "CPU"
        and (backend == "NONE" or str(getattr(device, "type", "")) == backend)
    ]
    if backend != "NONE" and devices:
        scene.cycles.device = "GPU"
        names = ", ".join(str(device.name) for device in devices[:2])
        return f"{backend} GPU: {names}"
    scene.cycles.device = "CPU"
    return "Cycles CPU • choose OptiX in Preferences > System for RTX"


def _configure_eevee(scene):
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.use_persistent_data = False
    eevee = getattr(scene, "eevee", None)
    for name, value in (
        ("taa_samples", 16),
        ("taa_render_samples", 96),
        ("use_taa_reprojection", True),
        ("use_shadow_jitter_viewport", False),
        ("shadow_pool_size", "512"),
        ("shadow_ray_count", 3),
        ("use_raytracing", False),
        ("use_fast_gi", True),
        ("fast_gi_method", "AMBIENT_OCCLUSION_ONLY"),
        ("fast_gi_quality", 0.75),
    ):
        _set_if_present(eevee, name, value)
    _set_studio_shadow_quality(scene, beauty=False)


def _configure_cycles(scene):
    scene.render.engine = "CYCLES"
    scene.render.use_persistent_data = True
    cycles = scene.cycles
    for name, value in (
        ("samples", 256),
        ("preview_samples", 32),
        ("use_denoising", True),
        ("denoiser", "OPENIMAGEDENOISE"),
        ("use_preview_denoising", True),
        ("preview_denoiser", "AUTO"),
        ("use_adaptive_sampling", True),
        ("adaptive_threshold", 0.01),
        ("adaptive_min_samples", 16),
        ("use_light_tree", True),
        ("texture_limit", "2048"),
        ("texture_limit_render", "OFF"),
        ("max_bounces", 8),
        ("diffuse_bounces", 4),
        ("glossy_bounces", 4),
        ("transmission_bounces", 8),
        ("transparent_max_bounces", 8),
        ("volume_bounces", 2),
        ("caustics_reflective", False),
        ("caustics_refractive", False),
        ("sample_clamp_direct", 0.0),
        ("sample_clamp_indirect", 10.0),
    ):
        _set_if_present(cycles, name, value)
    _set_studio_shadow_quality(scene, beauty=True)
    return _cycles_device(scene)


def _apply_render_quality(context, mode, *, switch_viewport=True):
    scene = context.scene
    _clear_legacy_preview_overrides()
    if mode == "ANIMATE":
        _configure_eevee(scene)
        count = _set_viewport_mode("ANIMATE") if switch_viewport else 0
        message = f"Fast Animation: Solid viewport in {count} view(s); materials stay untouched"
    elif mode == "PREVIEW":
        _configure_eevee(scene)
        count = _set_viewport_mode("PREVIEW") if switch_viewport else 0
        message = f"Fast Preview: Eevee + full materials in {count} view(s)"
    elif mode == "BEAUTY":
        device = _configure_cycles(scene)
        scene["xivblend_cycles_device"] = device
        count = _set_viewport_mode("BEAUTY") if switch_viewport else 0
        message = f"Beauty Photo: Cycles, adaptive 256 samples, denoise • {device}"
    else:
        raise ClipError(f"Unknown render quality preset: {mode}")
    scene["xivblend_render_quality"] = mode
    _set_render_status(message)
    return message


def _background_ramp(material):
    if material is None or not material.use_nodes or material.node_tree is None:
        return None
    named = material.node_tree.nodes.get(STUDIO_BACKGROUND_RAMP)
    if named is not None and named.type == "VALTORGB":
        return named
    return next(
        (node for node in material.node_tree.nodes if node.type == "VALTORGB"),
        None,
    )


def _apply_background_preset(scene, preset):
    colors = {
        "CHARCOAL": ((0.012, 0.022, 0.048, 1.0), (0.002, 0.006, 0.016, 1.0)),
        "NEUTRAL": ((0.075, 0.075, 0.075, 1.0), (0.018, 0.018, 0.018, 1.0)),
    }
    transparent = preset == "TRANSPARENT"
    scene.render.film_transparent = transparent
    for backdrop in _studio_backdrops(scene):
        backdrop.hide_render = transparent
        if transparent or not backdrop.data.materials:
            continue
        material = backdrop.data.materials[0]
        ramp = _background_ramp(material)
        if ramp is not None:
            lower, upper = colors.get(preset, colors["CHARCOAL"])
            ramp.color_ramp.elements[0].color = lower
            ramp.color_ramp.elements[-1].color = upper
            material.diffuse_color = lower
    scene["xivblend_background_preset"] = preset
    _apply_output_preset(scene, getattr(scene, "xivblend_output_preset", "HQ"))


def _apply_color_preset(scene, preset):
    if preset == "ACCURATE":
        scene.view_settings.view_transform = "Khronos PBR Neutral"
        scene.view_settings.look = "None"
        light_colors = {"key": (1.0, 1.0, 1.0), "fill": (1.0, 1.0, 1.0), "rim": (1.0, 1.0, 1.0)}
    else:
        scene.view_settings.view_transform = "AgX"
        try:
            scene.view_settings.look = "AgX - Medium High Contrast"
        except TypeError:
            scene.view_settings.look = "Medium High Contrast"
        light_colors = {
            "key": (1.0, 0.975, 0.955),
            "fill": (0.76, 0.86, 1.0),
            "rim": (0.66, 0.80, 1.0),
        }
    scene.view_settings.exposure = -0.40
    scene.view_settings.gamma = 1.0
    for light in _studio_lights(scene):
        role = str(light.get("xivblend_studio_role", ""))
        if role in light_colors:
            light.data.color = light_colors[role]
    scene["xivblend_color_preset"] = preset


def _apply_output_preset(scene, preset):
    settings = scene.render.image_settings
    if preset == "WEB":
        settings.file_format = "PNG"
        settings.color_depth = "8"
        settings.color_mode = "RGBA" if scene.render.film_transparent else "RGB"
        _set_if_present(settings, "compression", 25)
    elif preset == "EXR":
        settings.file_format = "OPEN_EXR"
        settings.color_depth = "16"
        settings.color_mode = "RGBA"
        _set_if_present(settings, "exr_codec", "PIZ")
    else:
        settings.file_format = "PNG"
        settings.color_depth = "16"
        settings.color_mode = "RGBA" if scene.render.film_transparent else "RGB"
        _set_if_present(settings, "compression", 35)
    scene["xivblend_output_preset"] = preset


def _update_render_quality(scene, context):
    try:
        _apply_render_quality(context, scene.xivblend_render_quality)
    except Exception as error:
        _set_render_status(str(error))


def _update_background_preset(scene, _context):
    try:
        _apply_background_preset(scene, scene.xivblend_background_preset)
    except Exception as error:
        _set_render_status(str(error))


def _update_color_preset(scene, _context):
    try:
        _apply_color_preset(scene, scene.xivblend_color_preset)
    except Exception as error:
        _set_render_status(str(error))


def _update_output_preset(scene, _context):
    try:
        _apply_output_preset(scene, scene.xivblend_output_preset)
    except Exception as error:
        _set_render_status(str(error))


def _execute_camera_fit(operator, context, sample_action):
    was_playing = _is_playback_running()
    if was_playing:
        _stop_playback()
    try:
        camera, subject, sample_count = _fit_character_camera(context, sample_action)
    except Exception as error:
        message = str(error)
        _set_render_status(message)
        operator.report({"ERROR"}, message)
        return {"CANCELLED"}
    finally:
        if was_playing:
            _start_playback()

    if sample_action:
        message = f"Camera fitted to {sample_count} samples of {subject}"
    else:
        message = f"Camera fitted to the current pose at frame {context.scene.frame_current}"
    _set_render_status(message)
    operator.report({"INFO"}, f"{message}; {camera.data.lens:g} mm lens preserved")
    return {"FINISHED"}


class XIVBLEND_OT_fit_camera_current_pose(Operator):
    bl_idname = "xivblend.fit_camera_current_pose"
    bl_label = "Fit Camera to Current Pose"
    bl_description = (
        "Reposition the portrait camera around the character's evaluated pose at the current frame; "
        "the camera lens and current frame are preserved"
    )
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        return _execute_camera_fit(self, context, sample_action=False)


class XIVBLEND_OT_fit_camera_active_action(Operator):
    bl_idname = "xivblend.fit_camera_active_action"
    bl_label = "Fit Camera to Whole Animation"
    bl_description = (
        "Sample the active character Action and fit every sampled pose inside the portrait frame; "
        "the camera lens, current frame, and playback state are preserved"
    )
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        return _execute_camera_fit(self, context, sample_action=True)


class XIVBLEND_OT_render_portrait(Operator):
    bl_idname = "xivblend.render_portrait"
    bl_label = "Render Portrait"
    bl_description = "Render the current frame without saving the image or the .blend file automatically"
    bl_options = {"REGISTER"}

    def execute(self, context):
        try:
            camera = _render_camera(context.scene)
            context.scene.camera = camera
            _apply_render_quality(
                context,
                context.scene.xivblend_render_quality,
                switch_viewport=False,
            )
            _set_render_status(f"Opening render for frame {context.scene.frame_current} with {camera.name}…")
            result = bpy.ops.render.render(
                "INVOKE_DEFAULT",
                animation=False,
                write_still=False,
                use_viewport=False,
            )
            if "CANCELLED" in result:
                _set_render_status("Render canceled")
                return {"CANCELLED"}
            _set_render_status(
                f"Render opened for frame {context.scene.frame_current} with {camera.name}"
            )
            return {"FINISHED"}
        except Exception as error:
            message = str(error)
            _set_render_status(message)
            self.report({"ERROR"}, message)
            return {"CANCELLED"}


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

        entries = _filtered_entries(context, catalog)
        custom_count = sum(1 for entry in entries if _entry_source_badge(entry))
        if custom_count:
            layout.label(text=f"{custom_count} custom animation(s) loaded", icon="PACKAGE")
        page_count = max(1, math.ceil(len(entries) / PAGE_SIZE))
        page = min(max(0, scene.xivblend_animation_page), page_count - 1)
        visible = entries[page * PAGE_SIZE:(page + 1) * PAGE_SIZE]
        if not visible:
            layout.label(text="No matching emotes", icon="INFO")
        else:
            grid = layout.grid_flow(row_major=True, columns=3, even_columns=True, even_rows=True, align=True)
            for entry in visible:
                icon_value = _icon_value(catalog, entry)
                badge = _entry_source_badge(entry)
                label = f"[{badge}] {_entry_name(entry)}" if badge else _entry_name(entry)
                kwargs = {"text": label[:22]}
                if icon_value:
                    kwargs["icon_value"] = icon_value
                else:
                    kwargs["icon"] = "PLAY"
                operator = grid.operator(XIVBLEND_OT_play_emote.bl_idname, **kwargs)
                operator.entry_id = _id_text(_entry_id(entry))
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
        for index, line in enumerate(_wrap_lines(status)):
            status_box.label(
                text=line,
                icon="ERROR" if index == 0 and "warning:" in status.casefold() else "NONE",
            )


def _active_effect_manifest(context):
    target = _target_armature(context)
    if target is None:
        return None, None
    session = _runtime_sessions.get(target.name)
    if not isinstance(session, dict):
        return None, None
    manifest = session.get("manifest")
    return (session, manifest) if isinstance(manifest, dict) else (session, None)


def _timing_text(event):
    start = _int_value(_field(event, "StartFrame"), 0)
    duration = max(0, _int_value(_field(event, "DurationFrames"), 0))
    kind = str(_field(event, "Kind", default="") or "").strip().casefold()
    if kind == "asyncvfx" and duration == 0:
        return f"starts frame {start} • native AVFX lifetime"
    return f"frames {start}–{start + duration}"


def _vfx_status_text(event):
    status = str(_field(event, "AssetStatus", default="") or "").strip()
    source_cached = bool(
        str(_field(event, "SourceRelativePath", default="") or "").strip()
    )
    descriptions = {
        "staticembeddedmeshpreview": (
            "Exact AVFX cached • static draw mesh available for inspection • Apricot playback pending"
        ),
        "unsupportedapricot": "Exact AVFX cached • requires XIV's Apricot particle runtime",
        "metadataonly": "Exact AVFX cached • metadata only; no independently drawable payload",
        "missingasset": "AVFX was not found in the live game data",
        "analysisfailed": "AVFX failed bounded structural analysis",
        "exportfailed": (
            "AVFX extraction was incomplete"
            if not source_cached
            else "Exact AVFX cached • optional preview export failed"
        ),
    }
    return descriptions.get(
        status.casefold(),
        status or ("Exact AVFX cached" if source_cached else "AVFX metadata unavailable"),
    )


def _vfx_particle_text(event):
    particles = [
        value for value in _as_list(_field(event, "ParticleTypes"))
        if isinstance(value, dict)
    ]
    labels = []
    for value in particles[:4]:
        name = str(_field(value, "TypeName", default="Unknown") or "Unknown")
        count = max(0, _int_value(_field(value, "Count"), 0))
        labels.append(f"{name} ×{count}")
    if len(particles) > 4:
        labels.append(f"+{len(particles) - 4} types")
    return ", ".join(labels)


class XIVBLEND_PT_emote_effects(Panel):
    bl_idname = "XIVBLEND_PT_emote_effects"
    bl_label = "Emote Effects"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "XivBlend"

    def draw(self, context):
        layout = self.layout
        session, manifest = _active_effect_manifest(context)
        if manifest is None:
            layout.label(text="Play an emote to inspect its effects", icon="INFO")
            return

        entry = session.get("entry", {}) if session else {}
        layout.label(text=_entry_name(entry), icon="ACTION")
        props = [
            value for value in _as_list(_field(manifest, "Props"))
            if isinstance(value, dict)
        ]
        effects = [
            value for value in _as_list(_field(manifest, "VisualEffects"))
            if isinstance(value, dict)
        ]
        sync = [value for value in effects if _is_sync_control_vfx(value)]
        native = [value for value in effects if not _is_sync_control_vfx(value)]
        cached_native = [
            value for value in native
            if str(_field(value, "SourceRelativePath", default="") or "").strip()
        ]
        static_previews = [
            value for value in native
            if str(_field(value, "AssetStatus", default="") or "").casefold()
            == "staticembeddedmeshpreview"
        ]

        summary = layout.box()
        summary.label(
            text=f"{len(props)} real prop event(s) • {len(native)} native AVFX event(s)",
            icon="SHADERFX",
        )
        if native:
            summary.label(
                text=f"{len(cached_native)} exact AVFX source(s) cached"
                f" • {len(static_previews)} static mesh preview(s)",
                icon="CHECKMARK" if len(cached_native) == len(native) else "INFO",
            )
            summary.label(text="TMB frames, color, scale, and placement are preserved")
        if sync:
            summary.label(
                text=f"{len(sync)} internal sync-control event(s) ignored correctly",
                icon="CHECKMARK",
            )

        if props:
            box = layout.box()
            box.label(text="Game Props", icon="OUTLINER_OB_MESH")
            for event in props[:8]:
                model = _int_value(_field(event, "ModelId"), 0)
                body = _int_value(_field(event, "BodyId"), 0)
                variant = _int_value(_field(event, "Variant"), 0)
                status = str(
                    _field(event, "_RuntimeStatus", "AssetStatus", default="Not prepared")
                    or "Not prepared"
                )
                loaded = status.casefold().startswith("loaded")
                row = box.row()
                row.label(
                    text=f"w{model:04d}/b{body:04d}/v{variant:04d} • {_timing_text(event)}",
                    icon="CHECKMARK" if loaded else "ERROR",
                )
                detail = box.row()
                detail.scale_y = 0.8
                bone = str(
                    _field(event, "_RuntimeBone", "AttachmentBone", default="") or ""
                )
                detail.label(text=f"{status}{f' • {bone}' if bone else ''}"[:92])
            if len(props) > 8:
                box.label(text=f"+ {len(props) - 8} more prop event(s)")

        if native:
            box = layout.box()
            box.label(text="Native AVFX", icon="PARTICLES")
            for event in native[:10]:
                game_path = str(_field(event, "GamePath", default="") or "")
                name = Path(game_path.replace("\\", "/")).name or "Unnamed AVFX"
                status = _vfx_status_text(event)
                source_cached = bool(
                    str(_field(event, "SourceRelativePath", default="") or "").strip()
                )
                box.label(
                    text=f"{name[:46]} • {_timing_text(event)}",
                    icon="INFO" if source_cached else "ERROR",
                )
                detail = box.row()
                detail.scale_y = 0.8
                detail.label(text=status[:92])
                models = max(0, _int_value(_field(event, "RenderableModelCount"), 0))
                vertices = max(0, _int_value(_field(event, "EmbeddedVertexCount"), 0))
                triangles = max(0, _int_value(_field(event, "EmbeddedTriangleCount"), 0))
                textures = len(_as_list(_field(event, "TextureReferences")))
                particles = _vfx_particle_text(event)
                if models or vertices or triangles or textures or particles:
                    metadata = []
                    if models:
                        metadata.append(f"{models} embedded mesh(es), {vertices} verts/{triangles} tris")
                    if textures:
                        metadata.append(f"{textures} texture ref(s)")
                    if particles:
                        metadata.append(particles)
                    meta_row = box.row()
                    meta_row.scale_y = 0.75
                    meta_row.label(text=" • ".join(metadata)[:92])
            if len(native) > 10:
                box.label(text=f"+ {len(native) - 10} more AVFX event(s)")

        if not props and not native:
            layout.label(text="This emote has no visible prop or native AVFX events", icon="CHECKMARK")


class XIVBLEND_PT_render_studio(Panel):
    bl_idname = "XIVBLEND_PT_render_studio"
    bl_label = "Render Studio"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "XivBlend"

    def draw(self, context):
        layout = self.layout
        target = _target_armature(context)
        try:
            camera = _render_camera(context.scene)
            camera_error = ""
        except Exception as error:
            camera = None
            camera_error = str(error)

        scene = context.scene
        quality = layout.box()
        quality.label(text="Quality", icon="SHADING_RENDERED")
        quality.prop(scene, "xivblend_render_quality", expand=True)
        descriptions = {
            "ANIMATE": "Solid viewport • fastest posing • no shader changes",
            "PREVIEW": "Eevee • real materials • quick preview",
            "BEAUTY": "Cycles • adaptive samples • denoised final image",
        }
        quality.label(text=descriptions.get(scene.xivblend_render_quality, ""), icon="INFO")

        appearance = layout.box()
        appearance.label(text="Look", icon="MATERIAL")
        appearance.prop(scene, "xivblend_background_preset", text="Background")
        appearance.prop(scene, "xivblend_color_preset", text="Color")
        appearance.prop(scene, "xivblend_output_preset", text="File")
        if scene.xivblend_render_quality == "BEAUTY":
            device = str(scene.get("xivblend_cycles_device", "Select Beauty to check the Cycles device"))
            appearance.label(text=device[:92], icon="PREFERENCES")

        header = layout.row()
        header.label(
            text=f"Camera: {camera.name}" if camera else "No usable portrait camera",
            icon="CAMERA_DATA" if camera else "ERROR",
        )

        pose_row = layout.row()
        pose_row.enabled = target is not None and camera is not None
        pose_row.operator(XIVBLEND_OT_fit_camera_current_pose.bl_idname, icon="CAMERA_DATA")

        action = target.animation_data.action if target and target.animation_data else None
        action_row = layout.row()
        action_row.enabled = target is not None and camera is not None and action is not None
        action_row.operator(XIVBLEND_OT_fit_camera_active_action.bl_idname, icon="ACTION")

        render_row = layout.row()
        render_row.enabled = camera is not None
        render_row.scale_y = 1.35
        render_row.operator(XIVBLEND_OT_render_portrait.bl_idname, icon="RENDER_STILL")

        aspect = (
            scene.render.resolution_x * scene.render.pixel_aspect_x
            / max(scene.render.resolution_y * scene.render.pixel_aspect_y, 1.0e-6)
        )
        if abs(aspect - 0.8) <= 0.01:
            layout.label(text="4:5 portrait output • lens stays unchanged", icon="INFO")
        else:
            layout.label(
                text=(
                    f"Output: {scene.render.resolution_x} × "
                    f"{scene.render.resolution_y} • lens stays unchanged"
                ),
                icon="INFO",
            )

        status = camera_error or getattr(context.window_manager, "xivblend_render_status", "") or _render_status
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
    _remove_runtime_visuals()


def _resume_after_save():
    global _save_sessions
    sessions, _save_sessions = _save_sessions, []
    for session in sessions:
        target = bpy.data.objects.get(session["target"])
        asset_kind = session.get("asset_kind", "clip")
        asset_path = Path(
            session.get("bundle_path") if asset_kind == "bundle" else session["clip_path"]
        )
        if target is None or not asset_path.is_file():
            continue
        try:
            catalog = _load_catalog()
            _import_animation_asset(
                bpy.context, target, asset_kind, asset_path, catalog,
                session["entry"], session["variant"],
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
        if not bpy.app.timers.is_registered(_resume_after_save):
            bpy.app.timers.register(_resume_after_save, first_interval=0.1)


@persistent
def _load_post_handler(_filepath):
    global _catalog, _catalog_signature, _save_sessions
    _stop_playback()
    _purge_transient_actions(restore=True)
    _remove_runtime_visuals()
    _pending_requests.clear()
    _captured_actions.clear()
    _scene_settings.clear()
    _action_settings.clear()
    _runtime_sessions.clear()
    _save_sessions = []
    _catalog = None
    _catalog_signature = None
    _close_previews()
    _clear_legacy_preview_overrides()
    _set_status("Ready")
    _set_render_status("Choose a render mode, fit the camera, then render the current frame.")


_CLASSES = (
    XIVBLEND_OT_refresh_animations,
    XIVBLEND_OT_set_animation_category,
    XIVBLEND_MT_animation_categories,
    XIVBLEND_OT_change_animation_page,
    XIVBLEND_OT_play_emote,
    XIVBLEND_OT_restore_captured_pose,
    XIVBLEND_OT_fit_camera_current_pose,
    XIVBLEND_OT_fit_camera_active_action,
    XIVBLEND_OT_render_portrait,
    XIVBLEND_PT_animation_browser,
    XIVBLEND_PT_emote_effects,
    XIVBLEND_PT_render_studio,
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
    bpy.types.Scene.xivblend_render_quality = EnumProperty(
        name="Render quality",
        description="Choose a lightweight viewport, a fast Eevee preview, or a Cycles final render",
        items=(
            ("ANIMATE", "Animate", "Fast Solid viewport; keeps every material untouched", "PLAY", 0),
            ("PREVIEW", "Preview", "Fast Eevee render with the real materials", "SHADING_RENDERED", 1),
            ("BEAUTY", "Beauty", "Cycles final render with adaptive sampling and denoising", "RENDER_STILL", 2),
        ),
        default="PREVIEW",
        update=_update_render_quality,
    )
    bpy.types.Scene.xivblend_background_preset = EnumProperty(
        name="Background",
        description="Use a repeatable branded, neutral, or transparent background",
        items=(
            ("CHARCOAL", "Charcoal Brand", "XivBlend's dark blue-charcoal studio sweep"),
            ("NEUTRAL", "Neutral Gray", "Neutral gray sweep for judging very dark and light mods"),
            ("TRANSPARENT", "Transparent", "Hide the sweep and render a transparent background"),
        ),
        default="CHARCOAL",
        update=_update_background_preset,
    )
    bpy.types.Scene.xivblend_color_preset = EnumProperty(
        name="Color",
        description="Choose an attractive highlight roll-off or neutral product-style colors",
        items=(
            ("BEAUTY", "Beauty (AgX)", "Cinematic highlight roll-off with subtly colored studio lights"),
            ("ACCURATE", "Accurate Mod Colors", "Khronos PBR Neutral with white studio lights"),
        ),
        default="BEAUTY",
        update=_update_color_preset,
    )
    bpy.types.Scene.xivblend_output_preset = EnumProperty(
        name="File",
        description="Choose a normal web image, high-quality PNG, or editing master",
        items=(
            ("WEB", "Web PNG (8-bit)", "Smaller display-ready PNG"),
            ("HQ", "High Quality PNG (16-bit)", "High-quality display-ready PNG"),
            ("EXR", "Editing Master (EXR)", "Half-float OpenEXR with preserved scene-linear range"),
        ),
        default="HQ",
        update=_update_output_preset,
    )
    bpy.types.WindowManager.xivblend_animation_status = StringProperty(
        name="XivBlend animation status",
        default="Ready",
        options={"SKIP_SAVE"},
    )
    bpy.types.WindowManager.xivblend_render_status = StringProperty(
        name="XivBlend render status",
        default="Choose a render mode, fit the camera, then render the current frame.",
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
    _remove_runtime_visuals()
    _pending_requests.clear()
    _action_settings.clear()
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
        (bpy.types.Scene, "xivblend_render_quality"),
        (bpy.types.Scene, "xivblend_background_preset"),
        (bpy.types.Scene, "xivblend_color_preset"),
        (bpy.types.Scene, "xivblend_output_preset"),
        (bpy.types.WindowManager, "xivblend_animation_status"),
        (bpy.types.WindowManager, "xivblend_render_status"),
    ):
        if hasattr(owner, name):
            delattr(owner, name)
    for cls in reversed(_CLASSES):
        bpy.utils.unregister_class(cls)
    _close_previews()


if __name__ == "__main__":
    register()
