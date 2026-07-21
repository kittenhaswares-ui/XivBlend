"""Build a self-contained Blender character file from a rigged glTF asset.

This script is intended to be run by Blender, not by the system Python::

    blender --background --factory-startup --python-exit-code 1 \
      --python build_blend.py -- \
      --input character.glb --manifest snapshot.json --output character.blend
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import sys
from typing import Any, Iterable
import warnings

import bpy
from bpy_extras.object_utils import world_to_camera_view
from mathutils import Matrix, Vector


BUILDER_NAME = "XivBlend Blender Builder"
BUILDER_VERSION = "0.5.0"
ANIMATION_CATALOG_SCHEMA = 1
MANIFEST_TEXT_NAME = "XIVBLEND_PROVENANCE.json"
BUILD_REPORT_TEXT_NAME = "XIVBLEND_BUILD_REPORT.json"
README_TEXT_NAME = "README_XIVBLEND.txt"
MAX_MANIFEST_BYTES = 16 * 1024 * 1024

PLACEHOLDER_MATERIAL_RE = re.compile(r"(?:null|error)(?:\.\d+)*", re.IGNORECASE)
WINDOWS_ABSOLUTE_PATH_RE = re.compile(
    r"(?:^|[\s\"'=])(?:\\\\\?\\)?[a-z]:[\\/]", re.IGNORECASE
)

CHARACTER_COLLECTION = "FFXIV Character"
RIG_COLLECTION = "Rig"
MESH_COLLECTION = "Meshes"
EXTRAS_COLLECTION = "Character Extras"
SETUP_COLLECTION = "Scene Setup"
CUSTOM_BONE_SHAPE_PROPERTY = "xivblend_custom_bone_shape"

POSE_REST_FRAME = 0
POSE_CAPTURED_FRAME = 100
POSE_SAMPLE_FRAMES = (0, 25, 50, 75, 100)
STUDIO_CAMERA_SAMPLE_FRAMES = (POSE_CAPTURED_FRAME,)
POSE_REST_MARKER = "XIV A-POSE"
POSE_CAPTURED_MARKER = "CAPTURED POSE"
POSE_MATRIX_TOLERANCE = 1.0e-5


class BuildError(RuntimeError):
    """A user-actionable build failure."""


def log(event: str, **details: Any) -> None:
    payload = {"event": event, **details}
    print(f"[xivblend] {json.dumps(payload, ensure_ascii=False, sort_keys=True)}", flush=True)


def blender_arguments() -> list[str]:
    try:
        separator = sys.argv.index("--")
    except ValueError:
        return []
    return sys.argv[separator + 1 :]


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Import a rigged glTF character and save a packed Blender file."
    )
    parser.add_argument("--input", required=True, help="Source .glb or .gltf file")
    parser.add_argument("--manifest", required=True, help="Snapshot manifest JSON file")
    parser.add_argument("--output", required=True, help="Destination .blend file")
    parser.add_argument(
        "--meddle-tools",
        help=(
            "Optional MeddleTools add-on directory. When supplied, FFXIV shader "
            "custom properties are mapped through its bundled shader library."
        ),
    )
    return parser.parse_args(blender_arguments())


def validate_paths(arguments: argparse.Namespace) -> tuple[Path, Path, Path]:
    source = Path(arguments.input).expanduser().resolve()
    manifest = Path(arguments.manifest).expanduser().resolve()
    output = Path(arguments.output).expanduser().resolve()

    if not source.is_file():
        raise BuildError(f"Input model does not exist: {source}")
    if source.suffix.lower() not in {".glb", ".gltf"}:
        raise BuildError("Input model must be a .glb or .gltf file")
    if not manifest.is_file():
        raise BuildError(f"Snapshot manifest does not exist: {manifest}")
    if output.suffix.lower() != ".blend":
        raise BuildError("Output path must end in .blend")
    if output == source or output == manifest:
        raise BuildError("Output must not overwrite the input model or manifest")

    output.parent.mkdir(parents=True, exist_ok=True)
    return source, manifest, output


def read_manifest(path: Path) -> tuple[dict[str, Any], str, str]:
    size = path.stat().st_size
    if size > MAX_MANIFEST_BYTES:
        raise BuildError(
            f"Snapshot manifest is too large ({size} bytes; maximum is {MAX_MANIFEST_BYTES})"
        )

    try:
        raw = path.read_bytes()
        document = json.loads(raw.decode("utf-8-sig"))
    except UnicodeDecodeError as exc:
        raise BuildError("Snapshot manifest must be UTF-8 JSON") from exc
    except json.JSONDecodeError as exc:
        raise BuildError(
            f"Snapshot manifest is invalid JSON at line {exc.lineno}, column {exc.colno}"
        ) from exc

    if not isinstance(document, dict):
        raise BuildError("Snapshot manifest root must be a JSON object")

    canonical = json.dumps(document, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    digest = hashlib.sha256(canonical.encode("utf-8")).hexdigest()
    return document, canonical, digest


def remove_datablocks(datablocks: Iterable[Any]) -> None:
    for datablock in list(datablocks):
        try:
            datablocks.remove(datablock, do_unlink=True)
        except TypeError:
            datablocks.remove(datablock)
        except RuntimeError:
            # Some built-in data (for example Render Result) cannot be removed.
            pass


def clear_scene() -> bpy.types.Scene:
    """Remove all content that could leak in from a non-factory startup file."""
    scene = bpy.context.scene

    if bpy.context.object is not None and bpy.context.object.mode != "OBJECT":
        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except RuntimeError:
            pass

    remove_datablocks(bpy.data.objects)
    remove_datablocks(bpy.data.collections)

    # Unused datablocks can still be saved when they have fake users. Remove the
    # common asset/script types explicitly so the result contains only this build.
    for name in (
        "actions",
        "armatures",
        "cameras",
        "curves",
        "fonts",
        "images",
        "lights",
        "materials",
        "meshes",
        "node_groups",
        "sounds",
        "texts",
        "worlds",
    ):
        remove_datablocks(getattr(bpy.data, name))

    for other_scene in list(bpy.data.scenes):
        if other_scene != scene:
            bpy.data.scenes.remove(other_scene, do_unlink=True)

    for key in list(scene.keys()):
        del scene[key]

    scene.name = "FFXIV Character"
    scene.frame_start = 0
    scene.frame_end = 0
    scene.frame_set(0)
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    scene.unit_settings.length_unit = "METERS"
    return scene


def validate_character_rig(
    imported: Iterable[bpy.types.Object],
) -> tuple[list[bpy.types.Object], list[bpy.types.Object]]:
    """Require every imported character mesh to be usefully bound to the rig."""
    imported = list(imported)
    armatures = [obj for obj in imported if obj.type == "ARMATURE"]
    if not armatures:
        raise BuildError("The glTF file did not contain a character armature")

    armature_set = set(armatures)
    custom_shapes = bone_custom_shapes(imported)
    character_meshes = [
        obj for obj in imported if obj.type == "MESH" and obj not in custom_shapes
    ]
    if not character_meshes:
        raise BuildError("The glTF file did not contain any character meshes")

    unbound: list[str] = []
    unweighted: dict[str, int] = {}
    for obj in character_meshes:
        target_armatures = {
            modifier.object
            for modifier in obj.modifiers
            if modifier.type == "ARMATURE" and modifier.object in armature_set
        }
        if not target_armatures:
            unbound.append(obj.name)
            continue

        valid_bone_names = {
            bone.name
            for armature in target_armatures
            for bone in armature.data.bones
        }
        group_names = {group.index: group.name for group in obj.vertex_groups}
        missing = 0
        for vertex in obj.data.vertices:
            if not any(
                assignment.weight > 1.0e-8
                and group_names.get(assignment.group) in valid_bone_names
                for assignment in vertex.groups
            ):
                missing += 1
        if missing:
            unweighted[obj.name] = missing

    if unbound:
        raise BuildError(
            "Character meshes are not bound to the imported armature: "
            + ", ".join(sorted(unbound))
        )
    if unweighted:
        raise BuildError(
            "Character meshes contain vertices without valid rig weights: "
            + ", ".join(
                f"{name} ({count})" for name, count in sorted(unweighted.items())
            )
        )

    return armatures, character_meshes


def remove_empty_vertex_groups(meshes: Iterable[bpy.types.Object]) -> int:
    """Remove inert glTF joint groups without changing any nonzero weights."""
    meshes = list(meshes)
    removed = 0
    for obj in meshes:
        weighted_indices = {
            assignment.group
            for vertex in obj.data.vertices
            for assignment in vertex.groups
            if assignment.weight > 1.0e-8
        }
        empty_groups = [
            group for group in obj.vertex_groups if group.index not in weighted_indices
        ]
        for group in reversed(empty_groups):
            obj.vertex_groups.remove(group)
        removed += len(empty_groups)
    log("empty_vertex_groups_removed", groups=removed, meshes=len(meshes))
    return removed


def set_scene_frame(scene: bpy.types.Scene, frame: float) -> None:
    whole = math.floor(frame)
    scene.frame_set(int(whole), subframe=frame - whole)
    bpy.context.view_layer.update()


def action_fcurves_for_armature(
    armature: bpy.types.Object,
) -> tuple[bpy.types.Action, list[bpy.types.FCurve]]:
    animation_data = armature.animation_data
    action = animation_data.action if animation_data is not None else None
    action_slot = (
        animation_data.action_slot
        if animation_data is not None and hasattr(animation_data, "action_slot")
        else None
    )
    if action is None or action_slot is None:
        raise BuildError(
            f"Armature {armature.name} has no captured glTF pose action"
        )

    fcurves: list[bpy.types.FCurve] = []
    for layer in action.layers:
        for strip in layer.strips:
            channelbag = strip.channelbag(action_slot, ensure=False)
            if channelbag is not None:
                fcurves.extend(channelbag.fcurves)
    if not fcurves:
        raise BuildError(
            f"Armature {armature.name} captured action contains no transform curves"
        )
    return action, fcurves


def ensure_captured_pose_action(armature: bpy.types.Object) -> bool:
    """Create a one-frame capture when a valid glTF contains no animation clip."""
    animation_data = armature.animation_data_create()
    if animation_data.action is not None:
        return False

    action = bpy.data.actions.new(f"{armature.name} Captured Pose")
    animation_data.action = action
    for pose_bone in armature.pose.bones:
        if pose_bone.rotation_mode != "QUATERNION":
            location, rotation, scale = pose_bone.matrix_basis.decompose()
            pose_bone.location = location
            pose_bone.rotation_mode = "QUATERNION"
            pose_bone.rotation_quaternion = rotation
            pose_bone.scale = scale
        for property_name in ("location", "rotation_quaternion", "scale"):
            pose_bone.keyframe_insert(
                data_path=property_name,
                frame=float(POSE_REST_FRAME),
                group=pose_bone.name,
            )
    set_scene_frame(bpy.context.scene, POSE_REST_FRAME)
    return True


def rest_channel_value(fcurve: bpy.types.FCurve) -> float:
    property_name = fcurve.data_path.rsplit(".", 1)[-1]
    if not fcurve.data_path.startswith('pose.bones["'):
        raise BuildError(
            f"Captured action contains unsupported channel: {fcurve.data_path}"
        )
    if property_name == "location":
        return 0.0
    if property_name == "scale":
        return 1.0
    if property_name == "rotation_quaternion":
        return 1.0 if fcurve.array_index == 0 else 0.0
    raise BuildError(
        "Captured action contains an unsupported pose transform channel: "
        f"{fcurve.data_path}"
    )


def configure_armatures(
    scene: bpy.types.Scene, armatures: Iterable[bpy.types.Object]
) -> tuple[dict[bpy.types.Object, dict[str, Matrix]], dict[str, Any]]:
    """Create a script-free Timeline blend from the rig rest pose to the capture."""
    armatures = sorted(armatures, key=lambda item: item.name)
    captured_pose: dict[bpy.types.Object, dict[str, Matrix]] = {}
    action_indices: dict[bpy.types.Action, int] = {}
    curve_count = 0
    keyed_bones: set[tuple[str, str]] = set()
    canonicalized_quaternions = 0
    custom_shapes_cleared = 0
    synthesized_actions = 0

    for armature in armatures:
        armature.data.display_type = "STICK"
        armature.data.pose_position = "POSE"
        armature.data.show_bone_custom_shapes = False
        armature.data.show_axes = False
        armature.data.show_names = False
        armature.show_in_front = True
        for pose_bone in armature.pose.bones:
            if pose_bone.custom_shape is not None:
                pose_bone.custom_shape[CUSTOM_BONE_SHAPE_PROPERTY] = True
                pose_bone.custom_shape.hide_render = True
                pose_bone.custom_shape.hide_viewport = True
                pose_bone.custom_shape = None
                custom_shapes_cleared += 1

        if ensure_captured_pose_action(armature):
            synthesized_actions += 1
        action, fcurves = action_fcurves_for_armature(armature)
        source_frames = [
            float(point.co.x)
            for fcurve in fcurves
            for point in fcurve.keyframe_points
        ]
        if (
            any(len(fcurve.keyframe_points) != 1 for fcurve in fcurves)
            or not source_frames
            or max(source_frames) - min(source_frames) > 1.0e-5
        ):
            raise BuildError(
                f"Armature {armature.name} pose action is not a one-frame capture"
            )

        source_frame = source_frames[0]
        set_scene_frame(scene, source_frame)
        captured_pose[armature] = {
            pose_bone.name: pose_bone.matrix_basis.copy()
            for pose_bone in armature.pose.bones
        }

        quaternion_curves: dict[str, dict[int, bpy.types.FCurve]] = {}
        for fcurve in fcurves:
            property_name = fcurve.data_path.rsplit(".", 1)[-1]
            rest_channel_value(fcurve)
            if property_name == "rotation_quaternion":
                quaternion_curves.setdefault(fcurve.data_path, {})[
                    fcurve.array_index
                ] = fcurve
            keyed_bones.add((armature.name, fcurve.data_path.rsplit(".", 1)[0]))

        flipped_paths: set[str] = set()
        for data_path, components in quaternion_curves.items():
            if set(components) != {0, 1, 2, 3}:
                raise BuildError(
                    f"Captured quaternion is incomplete on {armature.name}: {data_path}"
                )
            if components[0].keyframe_points[0].co.y < 0.0:
                flipped_paths.add(data_path)
        canonicalized_quaternions += len(flipped_paths)

        for fcurve in fcurves:
            captured_key = fcurve.keyframe_points[0]
            captured_value = float(captured_key.co.y)
            if fcurve.data_path in flipped_paths:
                captured_value = -captured_value
            captured_key.co = (float(POSE_CAPTURED_FRAME), captured_value)
            captured_key.interpolation = "LINEAR"
            rest_key = fcurve.keyframe_points.insert(
                float(POSE_REST_FRAME),
                rest_channel_value(fcurve),
                options={"FAST"},
            )
            rest_key.interpolation = "LINEAR"
            fcurve.extrapolation = "CONSTANT"
            fcurve.update()
        curve_count += len(fcurves)

        if action not in action_indices:
            action_indices[action] = len(action_indices)
            index = action_indices[action]
            action.name = (
                "XivBlend | A-Pose to Captured Pose"
                if index == 0
                else f"XivBlend | A-Pose to Captured Pose {index + 1}"
            )
            if hasattr(action, "use_frame_range"):
                action.use_frame_range = False

    for marker in list(scene.timeline_markers):
        scene.timeline_markers.remove(marker)
    scene.timeline_markers.new(POSE_REST_MARKER, frame=POSE_REST_FRAME)
    scene.timeline_markers.new(POSE_CAPTURED_MARKER, frame=POSE_CAPTURED_FRAME)
    scene.frame_start = POSE_REST_FRAME
    scene.frame_end = POSE_CAPTURED_FRAME
    set_scene_frame(scene, POSE_CAPTURED_FRAME)

    report = {
        "Type": "Timeline pose slider",
        "RestFrame": POSE_REST_FRAME,
        "CapturedFrame": POSE_CAPTURED_FRAME,
        "SampleFrames": list(POSE_SAMPLE_FRAMES),
        "Actions": len(action_indices),
        "FCurves": curve_count,
        "KeyedBones": len(keyed_bones),
        "CanonicalizedQuaternionBones": canonicalized_quaternions,
        "SynthesizedMissingCaptureActions": synthesized_actions,
        "ImporterCustomShapesCleared": custom_shapes_cleared,
    }
    log("pose_slider_configured", **report)
    return captured_pose, report


def import_character(
    source: Path,
) -> tuple[list[bpy.types.Object], list[bpy.types.Object], list[bpy.types.Object]]:
    before = set(bpy.data.objects)
    result = bpy.ops.import_scene.gltf(
        filepath=str(source),
        disable_bone_shape=True,
    )
    if "FINISHED" not in result:
        raise BuildError(f"Blender could not import {source.name}")

    imported = [obj for obj in bpy.data.objects if obj not in before]
    if not imported:
        raise BuildError("The glTF file did not contain any importable objects")
    armatures, character_meshes = validate_character_rig(imported)

    log(
        "model_imported",
        file=source.name,
        objects=len(imported),
        meshes=sum(obj.type == "MESH" for obj in imported),
        armatures=len(armatures),
        bound_meshes=len(character_meshes),
    )
    return imported, armatures, character_meshes


def resolve_meddle_tools(path_value: str | None) -> tuple[Path, Path] | None:
    if not path_value:
        return None

    requested = Path(path_value).expanduser().resolve()
    candidates = (requested, requested / "MeddleTools")
    package = next(
        (
            candidate
            for candidate in candidates
            if (candidate / "__init__.py").is_file()
            and (candidate / "shaders.blend").is_file()
        ),
        None,
    )
    if package is None:
        raise BuildError(
            "--meddle-tools must name the MeddleTools package directory, or its parent"
        )
    return package, package.parent


def apply_meddle_materials(
    source: Path,
    imported: list[bpy.types.Object],
    meddle_location: tuple[Path, Path] | None,
) -> dict[str, Any]:
    material_slots: dict[bpy.types.Material, list[bpy.types.MaterialSlot]] = {}
    for obj in imported:
        if obj.type != "MESH":
            continue
        for slot in obj.material_slots:
            if slot.material is not None:
                material_slots.setdefault(slot.material, []).append(slot)

    expected_materials = {
        material
        for material in material_slots
        if material.get("ShaderPackage") is not None
        and str(material.get("ShaderPackage")).strip()
    }
    if meddle_location is None:
        unmapped = sorted(
            f"{material.name} ({material.get('ShaderPackage')})"
            for material in expected_materials
        )
        mapping_report = {
            "source_materials": len(material_slots),
            "expected": len(expected_materials),
            "mapped": 0,
            "unmapped": unmapped,
        }
        log("gltf_material_mapping_checked", **mapping_report)
        if unmapped:
            raise BuildError(
                "The glTF contains FFXIV ShaderPackage materials, but MeddleTools "
                "was not supplied: "
                + ", ".join(unmapped)
            )
        return mapping_report

    package, package_parent = meddle_location
    sys.path.insert(0, str(package_parent))
    try:
        from MeddleTools import blend_import, version
        from MeddleTools.node_setup import node_configs

        # Calling the two data functions directly avoids UI operators, extension
        # installation, preferences, timers, and MeddleTools' network version check.
        version.updateCurrentRelease()
        blend_import.import_shaders()

        mapped = 0
        unmapped: list[str] = []
        cache_directory = str(source.parent / "cache")
        for material, slots in material_slots.items():
            node_configs.map_mesh(material, slots, cache_directory)
            if material not in expected_materials:
                continue

            replacements = [slot.material for slot in slots]
            mapping_succeeded = all(
                replacement is not None
                and replacement is not material
                and replacement.node_tree is not None
                for replacement in replacements
            )
            if mapping_succeeded:
                mapped += 1
            else:
                shader_package = str(material.get("ShaderPackage"))
                unmapped.append(f"{material.name} ({shader_package})")

        mapping_report = {
            "source_materials": len(material_slots),
            "expected": len(expected_materials),
            "mapped": mapped,
            "unmapped": sorted(unmapped),
        }
        log("meddle_material_mapping_checked", **mapping_report)
        if unmapped:
            raise BuildError(
                "MeddleTools could not map required FFXIV materials: "
                + ", ".join(sorted(unmapped))
            )

        # The library contains templates for many shaders. Only templates copied
        # into character slots belong in this output; purge the unused library data.
        used_materials = {
            slot.material
            for obj in imported
            if obj.type == "MESH"
            for slot in obj.material_slots
            if slot.material is not None
        }
        for material in list(bpy.data.materials):
            if material not in used_materials:
                bpy.data.materials.remove(material, do_unlink=True)
        try:
            bpy.ops.outliner.orphans_purge(
                do_local_ids=True, do_linked_ids=True, do_recursive=True
            )
        except (AttributeError, RuntimeError):
            pass

        log(
            "meddle_materials_applied",
            addon=str(package),
            source_materials=len(material_slots),
            expected_materials=len(expected_materials),
            mapped_materials=mapped,
            unmapped_materials=0,
        )
        return mapping_report
    except BuildError:
        raise
    except Exception as exc:
        raise BuildError(f"MeddleTools shader mapping failed: {exc}") from exc
    finally:
        try:
            sys.path.remove(str(package_parent))
        except ValueError:
            pass


def new_collection(name: str, parent: bpy.types.Collection) -> bpy.types.Collection:
    collection = bpy.data.collections.new(name)
    parent.children.link(collection)
    return collection


def bone_custom_shapes(objects: Iterable[bpy.types.Object]) -> set[bpy.types.Object]:
    return {
        pose_bone.custom_shape
        for armature in objects
        if armature.type == "ARMATURE" and armature.pose is not None
        for pose_bone in armature.pose.bones
        if pose_bone.custom_shape is not None
    }


def organize_objects(
    scene: bpy.types.Scene, imported: list[bpy.types.Object]
) -> dict[str, bpy.types.Collection]:
    root = new_collection(CHARACTER_COLLECTION, scene.collection)
    rig = new_collection(RIG_COLLECTION, root)
    meshes = new_collection(MESH_COLLECTION, root)
    extras = new_collection(EXTRAS_COLLECTION, root)
    setup = new_collection(SETUP_COLLECTION, scene.collection)

    targets = {
        "root": root,
        "rig": rig,
        "meshes": meshes,
        "extras": extras,
        "setup": setup,
    }

    custom_bone_shapes = bone_custom_shapes(imported) | {
        obj for obj in imported if bool(obj.get(CUSTOM_BONE_SHAPE_PROPERTY))
    }

    for obj in imported:
        if obj.type == "ARMATURE":
            target = rig
        elif obj.type == "MESH" and obj not in custom_bone_shapes:
            target = meshes
        elif obj.type in {"CAMERA", "LIGHT"}:
            target = setup
        else:
            target = extras

        if obj in custom_bone_shapes:
            # Blender's glTF importer may create visible meshes solely as bone
            # widgets. Preserve the rig reference, but never render the widget as
            # if it were another piece of the character.
            obj.hide_render = True
            obj.hide_viewport = True

        target.objects.link(obj)
        for old_collection in list(obj.users_collection):
            if old_collection != target:
                old_collection.objects.unlink(obj)

        obj["clean_extract_component"] = target.name

    retained = set(targets.values())
    for collection in list(bpy.data.collections):
        if collection not in retained:
            bpy.data.collections.remove(collection, do_unlink=True)

    return targets


def character_bounds(
    objects: Iterable[bpy.types.Object],
) -> tuple[Vector, Vector, list[Vector]]:
    objects = list(objects)
    custom_shapes = bone_custom_shapes(objects)
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    depsgraph.update()
    points: list[Vector] = []
    for obj in objects:
        if (
            obj.type != "MESH"
            or obj in custom_shapes
            or obj.hide_render
            or len(obj.data.polygons) == 0
            or not obj.bound_box
        ):
            continue
        evaluated = obj.evaluated_get(depsgraph)
        points.extend(
            evaluated.matrix_world @ Vector(corner) for corner in evaluated.bound_box
        )

    if not points:
        raise BuildError("No visible character mesh geometry was available for scene setup")

    minimum = Vector(tuple(min(point[axis] for point in points) for axis in range(3)))
    maximum = Vector(tuple(max(point[axis] for point in points) for axis in range(3)))
    return (minimum + maximum) * 0.5, maximum - minimum, points


def character_bounds_across_frames(
    scene: bpy.types.Scene,
    objects: Iterable[bpy.types.Object],
    frames: Iterable[int] = POSE_SAMPLE_FRAMES,
) -> tuple[Vector, Vector, list[Vector]]:
    """Return bounds covering both pose endpoints and useful blend samples."""
    objects = list(objects)
    points: list[Vector] = []
    try:
        for frame in frames:
            set_scene_frame(scene, frame)
            _, _, frame_points = character_bounds(objects)
            points.extend(frame_points)
    finally:
        set_scene_frame(scene, POSE_CAPTURED_FRAME)

    minimum = Vector(tuple(min(point[axis] for point in points) for axis in range(3)))
    maximum = Vector(tuple(max(point[axis] for point in points) for axis in range(3)))
    return (minimum + maximum) * 0.5, maximum - minimum, points


def point_at(obj: bpy.types.Object, target: Vector) -> None:
    direction = target - obj.location
    if direction.length_squared > 0.0:
        obj.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()


def fit_front_camera(
    scene: bpy.types.Scene,
    camera: bpy.types.Object,
    bounds: Iterable[Vector],
    target: Vector,
    margin: float = 1.08,
) -> float:
    """Place a front-facing perspective camera around the supplied bounds."""
    bounds = list(bounds)
    if not bounds:
        raise BuildError("Cannot fit the studio camera without character bounds")
    render_aspect = (
        scene.render.resolution_x
        * scene.render.pixel_aspect_x
        / max(scene.render.resolution_y * scene.render.pixel_aspect_y, 1.0e-6)
    )
    tan_half_x = max(math.tan(camera.data.angle_x * 0.5), 1.0e-4)
    tan_half_y = tan_half_x / max(render_aspect, 1.0e-4)
    distance = max(
        max(
            abs(point.x - target.x) * margin / tan_half_x - (point.y - target.y),
            abs(point.z - target.z) * margin / tan_half_y - (point.y - target.y),
        )
        for point in bounds
    )
    extent = max(
        max(point[axis] for point in bounds) - min(point[axis] for point in bounds)
        for axis in range(3)
    )
    distance = max(distance, extent * 0.75, 0.1)
    camera.location = target + Vector((0.0, -distance, 0.0))
    camera.data.clip_start = max(distance / 1000.0, 0.001)
    camera.data.clip_end = max(distance + extent * 20.0, 100.0)
    point_at(camera, target)
    return distance


def add_area_light(
    setup: bpy.types.Collection,
    name: str,
    location: Vector,
    target: Vector,
    energy: float,
    size: float,
    color: tuple[float, float, float],
    *,
    role: str,
    size_y: float | None = None,
    spread: float = math.pi,
) -> bpy.types.Object:
    light_data = bpy.data.lights.new(name, type="AREA")
    light_data.energy = energy
    light_data.color = color
    light_data.normalize = True
    light_data.shape = "RECTANGLE"
    light_data.size = size
    light_data.size_y = size if size_y is None else size_y
    light_data.spread = spread
    light_data.use_shadow = True
    light = bpy.data.objects.new(name, light_data)
    setup.objects.link(light)
    light.location = location
    point_at(light, target)
    light["xivblend_component"] = "studio_light"
    light["xivblend_studio_role"] = role
    return light


def create_studio_backdrop(
    setup: bpy.types.Collection,
    center: Vector,
    size: Vector,
    largest: float,
    floor_z: float | None = None,
) -> bpy.types.Object:
    """Create a removable curved studio sweep beneath and behind the character."""
    if floor_z is None:
        floor_z = center.z - size.z * 0.5
    floor_z -= max(largest * 0.002, 0.001)
    half_width = largest * 5.0
    # A coarse quarter-circle reads as horizontal bands once soft studio lights
    # rake across it.  Dense geometry keeps the sweep visually continuous even
    # in Eevee without a subdivision modifier.
    curve_steps = 64
    width_steps = 8
    radius = largest * 0.8
    curve_center_y = center.y + largest * 0.8
    curve_center_z = floor_z + radius
    profile = [(center.y - largest * 2.8, floor_z)]
    profile.extend(
        (
            curve_center_y + radius * math.cos(theta),
            curve_center_z + radius * math.sin(theta),
        )
        for theta in (
            -math.pi * 0.5 + math.pi * 0.5 * step / curve_steps
            for step in range(curve_steps + 1)
        )
    )
    profile.append((curve_center_y + radius, floor_z + largest * 6.0))
    vertices = [
        (x, y, z)
        for y, z in profile
        for x in (
            center.x - half_width + 2.0 * half_width * step / width_steps
            for step in range(width_steps + 1)
        )
    ]
    row = width_steps + 1
    faces = []
    for profile_index in range(len(profile) - 1):
        for width_index in range(width_steps):
            lower = profile_index * row + width_index
            upper = lower + row
            faces.append((lower, lower + 1, upper + 1, upper))

    mesh = bpy.data.meshes.new("XivBlend Studio Backdrop")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    for index, polygon in enumerate(mesh.polygons):
        # Keep the broad floor and back wall perfectly planar. Smooth only the
        # curved transition so Blender cannot expose a diagonal quad split.
        profile_segment = index // width_steps
        polygon.use_smooth = 0 < profile_segment < len(profile) - 2

    material = bpy.data.materials.new("XivBlend Studio Charcoal")
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", DeprecationWarning)
        material.use_nodes = True
    material.diffuse_color = (0.012, 0.017, 0.028, 1.0)
    principled = (
        material.node_tree.nodes.get("Principled BSDF")
        if material.node_tree is not None
        else None
    )
    if principled is not None:
        principled.inputs["Base Color"].default_value = material.diffuse_color
        principled.inputs["Roughness"].default_value = 0.82
        specular = principled.inputs.get("Specular IOR Level")
        if specular is not None:
            specular.default_value = 0.22
        nodes = material.node_tree.nodes
        links = material.node_tree.links
        output = nodes.get("Material Output")
        coordinates = nodes.new("ShaderNodeTexCoord")
        separate = nodes.new("ShaderNodeSeparateXYZ")
        color_ramp = nodes.new("ShaderNodeValToRGB")
        color_ramp.color_ramp.interpolation = "EASE"
        color_ramp.color_ramp.elements[0].position = 0.0
        color_ramp.color_ramp.elements[0].color = (0.012, 0.022, 0.048, 1.0)
        color_ramp.color_ramp.elements[1].position = 1.0
        color_ramp.color_ramp.elements[1].color = (0.002, 0.006, 0.016, 1.0)
        transition = nodes.new("ShaderNodeMapRange")
        transition.clamp = True
        if hasattr(transition, "interpolation_type"):
            transition.interpolation_type = "SMOOTHERSTEP"
        transition.inputs["From Min"].default_value = 0.055
        transition.inputs["From Max"].default_value = 0.19
        transition.inputs["To Min"].default_value = 0.0
        transition.inputs["To Max"].default_value = 1.0
        emission = nodes.new("ShaderNodeEmission")
        emission.inputs["Strength"].default_value = 1.0
        mix = nodes.new("ShaderNodeMixShader")
        links.new(coordinates.outputs["Generated"], separate.inputs["Vector"])
        links.new(separate.outputs["Z"], color_ramp.inputs["Fac"])
        links.new(separate.outputs["Z"], transition.inputs["Value"])
        links.new(color_ramp.outputs["Color"], principled.inputs["Base Color"])
        links.new(color_ramp.outputs["Color"], emission.inputs["Color"])
        links.new(transition.outputs["Result"], mix.inputs[0])
        links.new(principled.outputs["BSDF"], mix.inputs[1])
        links.new(emission.outputs["Emission"], mix.inputs[2])
        if output is not None:
            for existing in list(output.inputs["Surface"].links):
                links.remove(existing)
            links.new(mix.outputs["Shader"], output.inputs["Surface"])

    backdrop = bpy.data.objects.new("XivBlend Studio Backdrop", mesh)
    setup.objects.link(backdrop)
    backdrop.data.materials.append(material)
    backdrop["xivblend_component"] = "studio_backdrop"
    return backdrop


def configure_scene_setup(
    scene: bpy.types.Scene,
    imported: list[bpy.types.Object],
    setup: bpy.types.Collection,
) -> None:
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = 1440
    scene.render.resolution_y = 1800
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGB"
    scene.render.image_settings.color_depth = "16"
    scene.render.film_transparent = False
    if hasattr(scene, "eevee"):
        scene.eevee.taa_render_samples = 96
        scene.eevee.shadow_pool_size = "512"
        scene.eevee.shadow_ray_count = 3
        scene.eevee.use_raytracing = False
        scene.eevee.ray_tracing_method = "SCREEN"
        scene.eevee.use_fast_gi = True
        scene.eevee.fast_gi_method = "AMBIENT_OCCLUSION_ONLY"
        scene.eevee.fast_gi_quality = 0.75
    try:
        scene.view_settings.view_transform = "AgX"
        scene.view_settings.look = "AgX - Medium High Contrast"
    except TypeError:
        # Keep Blender's default transform when a future build renames the view.
        pass
    scene.view_settings.exposure = -0.55
    scene.view_settings.gamma = 1.0

    world = bpy.data.worlds.new("Neutral World")
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", DeprecationWarning)
        world.use_nodes = True
    background = world.node_tree.nodes.get("Background") if world.node_tree else None
    if background is not None:
        background.inputs["Color"].default_value = (0.004, 0.007, 0.014, 1.0)
        background.inputs["Strength"].default_value = 0.035
    scene.world = world

    center, size, _ = character_bounds_across_frames(scene, imported)
    largest = max(max(size), 0.1)
    if hasattr(scene, "eevee"):
        scene.eevee.fast_gi_distance = max(largest * 0.10, 0.05)

    # The Timeline's intermediate frames are a rig-editing convenience, not
    # authored character poses.  Fit the default render to the captured pose,
    # and anchor the floor to the armature object's world origin. FFXIV rigs
    # use that as their ground plane. The real endpoint geometry may raise the
    # floor by a small sole offset, but a low tail, robe or sheathed weapon must
    # not drag the sweep below the rig ground and make the feet appear to float.
    support_meshes = [
        obj
        for obj in imported
        if obj.type == "MESH"
        and re.search(r"(?:^|_)sho(?:_|$)", obj.name, re.IGNORECASE)
    ]
    support_bounds: list[Vector] = []
    set_scene_frame(scene, POSE_REST_FRAME)
    _, _, rest_bounds = character_bounds(imported)
    if support_meshes:
        try:
            _, _, frame_support_bounds = character_bounds(support_meshes)
            support_bounds.extend(frame_support_bounds)
        except BuildError:
            pass
    set_scene_frame(scene, POSE_CAPTURED_FRAME)
    captured_center, captured_size, captured_bounds = character_bounds(imported)
    if support_meshes:
        try:
            _, _, frame_support_bounds = character_bounds(support_meshes)
            support_bounds.extend(frame_support_bounds)
        except BuildError:
            pass
    character_armatures = [obj for obj in imported if obj.type == "ARMATURE"]
    if len(character_armatures) != 1:
        raise BuildError(
            "Scene setup requires exactly one character armature; "
            f"found {len(character_armatures)}"
        )
    rig_ground_z = float(character_armatures[0].matrix_world.translation.z)
    endpoint_minimum_z = min(point.z for point in rest_bounds + captured_bounds)
    support_minimum_z = (
        min(point.z for point in support_bounds)
        if support_bounds
        else endpoint_minimum_z
    )
    ground_plane_z = max(rig_ground_z, support_minimum_z)

    # Always create an unparented camera so an imported camera's hidden state,
    # constraints or parent transform cannot compromise deterministic framing.
    for source_camera in (obj for obj in imported if obj.type == "CAMERA"):
        source_camera.hide_render = True
        source_camera.hide_viewport = True
    camera_data = bpy.data.cameras.new("XivBlend Studio Camera")
    camera = bpy.data.objects.new("XivBlend Studio Camera", camera_data)
    setup.objects.link(camera)
    camera.data.lens = 70.0
    camera.data.sensor_fit = "HORIZONTAL"
    camera.data.dof.use_dof = False
    camera.hide_render = False
    camera.hide_viewport = False
    camera_target = captured_center + Vector((0.0, 0.0, captured_size.z * 0.015))
    fit_front_camera(scene, camera, captured_bounds, camera_target)
    camera["xivblend_component"] = "studio_camera"
    scene.camera = camera

    # Disable any glTF lights so every export gets the same predictable studio rig.
    for source_light in (obj for obj in imported if obj.type == "LIGHT"):
        source_light.hide_render = True
        source_light.hide_viewport = True

    light_target = captured_center + Vector((0.0, 0.0, captured_size.z * 0.16))
    add_area_light(
        setup,
        "Key Light (Warm Softbox)",
        captured_center + Vector((-largest * 1.15, -largest * 1.35, largest * 1.35)),
        light_target,
        260.0 * largest * largest,
        largest * 0.78,
        (1.0, 0.86, 0.76),
        role="key",
        size_y=largest * 1.20,
        spread=math.radians(115.0),
    )
    add_area_light(
        setup,
        "Fill Light (Cool Softbox)",
        captured_center + Vector((largest * 1.35, -largest * 0.95, largest * 0.75)),
        light_target,
        48.0 * largest * largest,
        largest * 1.50,
        (0.75, 0.82, 1.0),
        role="fill",
        size_y=largest * 1.85,
        spread=math.radians(125.0),
    )
    add_area_light(
        setup,
        "Rim Light (Cool Strip)",
        captured_center + Vector((largest * 0.85, largest * 0.95, largest * 1.35)),
        captured_center + Vector((0.0, 0.0, captured_size.z * 0.20)),
        82.0 * largest * largest,
        largest * 0.28,
        (0.65, 0.78, 1.0),
        role="rim",
        size_y=largest * 1.25,
        spread=math.radians(85.0),
    )
    create_studio_backdrop(setup, center, size, largest, ground_plane_z)
    bpy.context.view_layer.update()


def saved_view_3d_spaces() -> list[tuple[str, bpy.types.SpaceView3D]]:
    spaces: list[tuple[str, bpy.types.SpaceView3D]] = []
    for screen in bpy.data.screens:
        for area_index, area in enumerate(screen.areas):
            for space_index, space in enumerate(area.spaces):
                if space.type == "VIEW_3D":
                    spaces.append(
                        (f"{screen.name}/area-{area_index}/space-{space_index}", space)
                    )
    return spaces


def configure_viewport_defaults() -> dict[str, Any]:
    """Save a clean viewport without disabling any render camera or studio light."""
    spaces = saved_view_3d_spaces()
    if not spaces:
        raise BuildError("The Blender file has no saved 3D Viewport to configure")

    for _, space in spaces:
        overlay = space.overlay
        overlay.show_overlays = True
        overlay.show_extras = False
        overlay.show_relationship_lines = False
        overlay.show_floor = False
        overlay.show_ortho_grid = False
        overlay.show_axis_x = False
        overlay.show_axis_y = False
        overlay.show_axis_z = False
        overlay.show_camera_guides = False
        overlay.show_bones = True
        overlay.show_outline_selected = True

    report = {
        "SavedView3DSpaces": len(spaces),
        "CameraLightExtrasVisible": False,
        "RelationshipLinesVisible": False,
        "FloorGridVisible": False,
        "AxesVisible": False,
        "BonesVisible": True,
        "SelectionOutlineVisible": True,
    }
    log("viewport_defaults_configured", **report)
    return report


def select_primary_armature(
    armatures: Iterable[bpy.types.Object],
) -> bpy.types.Object:
    armatures = list(armatures)
    if not armatures:
        raise BuildError("No armature is available for the saved Blender selection")

    if bpy.context.object is not None and bpy.context.object.mode != "OBJECT":
        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except RuntimeError as exc:
            raise BuildError("Could not return Blender to Object Mode") from exc

    primary = max(
        armatures,
        key=lambda item: (len(item.data.bones), item.name.casefold()),
    )
    for obj in bpy.context.view_layer.objects:
        obj.select_set(False)
    primary.hide_set(False)
    primary.hide_viewport = False
    primary.select_set(True)
    bpy.context.view_layer.objects.active = primary
    if primary.mode != "OBJECT":
        raise BuildError("The primary character armature is not in Object Mode")
    return primary


def embed_manifest(
    scene: bpy.types.Scene,
    manifest_path: Path,
    document: dict[str, Any],
    canonical: str,
    digest: str,
    source: Path,
) -> None:
    # Keep sensitive Glamourer state and absolute mod paths in the external
    # diagnostic manifest. The shareable .blend receives only a compact summary
    # plus the canonical digest needed to identify the exact source snapshot.
    resource_paths = document.get(
        "PenumbraResourcePaths", document.get("penumbra_resource_paths", {})
    )
    warnings = document.get("Warnings", document.get("warnings", []))
    race_code, face_skeleton = read_animation_identity(document)
    summary = {
        "SchemaVersion": document.get(
            "SchemaVersion", document.get("schemaVersion", document.get("schema_version"))
        ),
        "CapturedAtUtc": document.get(
            "CapturedAtUtc", document.get("capturedAtUtc", document.get("captured_at_utc"))
        ),
        "Source": document.get("Source", document.get("source")),
        "RaceCode": race_code,
        "FaceSkeleton": face_skeleton,
        "AnimationCatalogSchema": ANIMATION_CATALOG_SCHEMA,
        "GlamourerStateCaptured": bool(
            document.get(
                "GlamourerStateBase64",
                document.get("glamourerStateBase64", document.get("glamourer_state_base64")),
            )
        ),
        "PenumbraResourceEntryCount": (
            len(resource_paths) if isinstance(resource_paths, dict) else 0
        ),
        "Warnings": warnings if isinstance(warnings, list) else [],
        "FullManifestExternal": manifest_path.name,
        "FullManifestSha256Canonical": digest,
    }
    redacted = json.dumps(summary, ensure_ascii=False, indent=2, sort_keys=True) + "\n"

    # A parsed JSON text block is inert data. In particular, never use eval/exec
    # and never give the block a .py name, even when manifest values are hostile.
    text = bpy.data.texts.new(MANIFEST_TEXT_NAME)
    text.write(redacted)
    text.use_fake_user = True
    text["content_type"] = "application/json"
    text["full_manifest_sha256_canonical"] = digest
    text["source_filename"] = manifest_path.name

    scene["xivblend_builder"] = BUILDER_NAME
    scene["xivblend_builder_version"] = BUILDER_VERSION
    scene["xivblend_manifest_text"] = MANIFEST_TEXT_NAME
    scene["xivblend_manifest_sha256_canonical"] = digest
    scene["xivblend_manifest_filename"] = manifest_path.name
    scene["xivblend_source_filename"] = source.name
    scene["xivblend_animation_catalog_schema"] = ANIMATION_CATALOG_SCHEMA
    if race_code is not None:
        scene["xivblend_race_code"] = race_code
    if face_skeleton is not None:
        scene["xivblend_face_skeleton"] = face_skeleton

    # Preserve the original prototype keys for scripts that inspected 0.0.2/0.0.3.
    scene["clean_extract_builder"] = BUILDER_NAME
    scene["clean_extract_builder_version"] = BUILDER_VERSION
    scene["clean_extract_manifest_text"] = MANIFEST_TEXT_NAME
    scene["clean_extract_manifest_sha256"] = digest
    scene["clean_extract_manifest_filename"] = manifest_path.name
    scene["clean_extract_source_filename"] = source.name

    schema_value = summary["SchemaVersion"]
    if isinstance(schema_value, (str, int, float)) and not isinstance(schema_value, bool):
        scene["xivblend_snapshot_schema"] = str(schema_value)[:128]
        scene["clean_extract_snapshot_schema"] = str(schema_value)[:128]


def read_animation_identity(
    document: dict[str, Any],
) -> tuple[int | None, str | None]:
    """Return only validated, path-free identifiers safe to embed in a .blend."""
    raw_race = document.get(
        "RaceCode", document.get("raceCode", document.get("race_code"))
    )
    if isinstance(raw_race, bool):
        race_code = None
    elif isinstance(raw_race, int):
        race_code = raw_race
    elif isinstance(raw_race, str) and re.fullmatch(r"\d{1,4}", raw_race.strip()):
        race_code = int(raw_race.strip())
    else:
        race_code = None
    if race_code is not None and not 1 <= race_code <= 9999:
        race_code = None

    raw_face = document.get(
        "FaceSkeleton",
        document.get("faceSkeleton", document.get("face_skeleton")),
    )
    face_skeleton = (
        raw_face.strip().lower() if isinstance(raw_face, str) else None
    )
    if face_skeleton is not None and re.fullmatch(r"f\d{4}", face_skeleton) is None:
        face_skeleton = None

    return race_code, face_skeleton


def apply_animation_identity(
    scene: bpy.types.Scene,
    primary_armature: bpy.types.Object,
) -> None:
    """Copy the safe animation lookup keys onto the add-on's primary target rig."""
    for key in (
        "xivblend_race_code",
        "xivblend_face_skeleton",
        "xivblend_animation_catalog_schema",
    ):
        if key in scene:
            primary_armature[key] = scene[key]


def write_embedded_readme() -> None:
    text = bpy.data.texts.new(README_TEXT_NAME)
    text.write(
        "XivBlend character export\n"
        "=========================\n\n"
        "- The Rig collection contains the original FFXIV bone hierarchy, names, "
        "rest transforms, pose and deformation weights reconstructed through glTF.\n"
        "- Use the Timeline at the bottom as the pose slider: frame 0 (XIV A-POSE) is "
        "the exact rig rest pose, frame 100 (CAPTURED POSE) is the exported in-game pose, "
        "and the frames between them blend smoothly. The file opens at frame 100.\n"
        "- The armature uses Blender's compact Stick display. Camera/light icons, grid, "
        "axes and relationship lines are hidden as viewport overlays, not disabled.\n"
        "- Press Numpad 0 for the generated camera and F12 to render. With the "
        "XivBlend Blender add-on installed, open the N sidebar and use XivBlend > "
        "Render Studio to fit the camera to the current pose or whole animation, then "
        "press Render Portrait.\n"
        "- The portrait camera, three lights and studio sweep are isolated in the "
        "Scene Setup collection and can be hidden or replaced without touching the character.\n"
        "- Native glTF rest/bind axes are preserved. For an FBX round trip, choose Primary "
        "Bone Axis X and Secondary Bone Axis Y at the FBX import/export boundary; do not "
        "remap this Blender armature in place.\n"
        "- When the XivBlend Animation Browser add-on is installed, open the sidebar "
        "with N and use XivBlend > Player Emotes to search by the in-game emote icons.\n"
        "- Animation clips are prepared on demand in XivBlend's shared local cache. The "
        "catalog, game icons and game animation assets are not embedded in this file; only "
        "an action you explicitly load is added to the character.\n"
        "- The animation browser is deliberately limited to vanilla player emotes and "
        "facial expressions. Combat actions, weapons and VFX are not included.\n"
        "- Textures used by the materials are packed into this file.\n"
        "- The external xivblend-manifest.json contains private character/mod paths and "
        "should not be shared casually. The embedded provenance is redacted.\n"
    )
    text.use_fake_user = True


def sensitive_path_value(value: str) -> bool:
    raw = value.strip()
    if not raw:
        return False

    normalized = raw.replace("\\", "/").lower()
    if normalized.startswith(("http://", "https://")):
        return False

    return (
        "/penumbra/" in normalized
        or normalized.startswith("penumbra/")
        or "/users/" in normalized
        or normalized.startswith("file://")
        or normalized.startswith("//?/")
        or raw.startswith("\\\\")
        or bool(WINDOWS_ABSOLUTE_PATH_RE.search(raw))
        # Blender's // prefix is a benign path relative to the .blend. A single
        # slash (or backslash after normalization) is an absolute filesystem root.
        or (normalized.startswith("/") and not normalized.startswith("//"))
    )


def saved_blender_ids() -> list[bpy.types.ID]:
    """Return every ID datablock reachable through bpy.data, without duplicates."""
    result: list[bpy.types.ID] = []
    seen: set[int] = set()
    for property_definition in bpy.data.bl_rna.properties:
        if getattr(property_definition, "type", None) != "COLLECTION":
            continue
        try:
            collection = getattr(bpy.data, property_definition.identifier)
        except (AttributeError, RuntimeError):
            continue
        try:
            candidates = list(collection)
        except (RuntimeError, TypeError):
            continue
        for datablock in candidates:
            if not isinstance(datablock, bpy.types.ID) or id(datablock) in seen:
                continue
            seen.add(id(datablock))
            result.append(datablock)

    # Scene master collections are IDs but are not exposed in bpy.data.collections.
    for scene in bpy.data.scenes:
        master_collection = scene.collection
        if id(master_collection) not in seen:
            seen.add(id(master_collection))
            result.append(master_collection)
    return result


def nested_strings(value: Any, seen: set[int] | None = None) -> Iterable[str]:
    """Yield strings from nested Blender IDProperty groups and arrays."""
    if isinstance(value, str):
        yield value
        return
    if isinstance(value, bytes):
        yield value.decode("utf-8", errors="replace")
        return
    if value is None or isinstance(value, (bool, int, float, bpy.types.ID)):
        return

    if seen is None:
        seen = set()
    marker = id(value)
    if marker in seen:
        return
    seen.add(marker)

    items = getattr(value, "items", None)
    if callable(items):
        try:
            pairs = list(items())
        except (RuntimeError, TypeError, ValueError):
            pairs = []
        for key, nested in pairs:
            if isinstance(key, str):
                yield key
            yield from nested_strings(nested, seen)
        return

    try:
        values = list(value)
    except (RuntimeError, TypeError, ValueError):
        return
    for nested in values:
        yield from nested_strings(nested, seen)


def value_contains_sensitive_path(value: Any) -> bool:
    return any(sensitive_path_value(item) for item in nested_strings(value))


def sanitize_packed_provenance(imported: Iterable[bpy.types.Object]) -> int:
    """Remove absolute/mod-folder paths after their image data has been packed."""
    redacted = 0
    # Material, mesh and object extras are the known Meddle sources, but scanning
    # every saved ID also covers future importer metadata and nested IDProperties.
    for datablock in saved_blender_ids():
        for key in list(datablock.keys()):
            value = datablock.get(key)
            key_lower = key.lower()
            if (
                key_lower == "modelfullpath"
                or sensitive_path_value(key)
                or value_contains_sensitive_path(value)
            ):
                del datablock[key]
                redacted += 1

    for index, image in enumerate(sorted(bpy.data.images, key=lambda item: item.name)):
        if not image_is_packed(image):
            continue
        suffix = Path(image.filepath).suffix or ".png"
        image.filepath = f"//textures/texture_{index:03d}{suffix}"

    log("private_paths_redacted", properties=redacted, images=len(bpy.data.images))
    return redacted


def sensitive_provenance_locations() -> list[str]:
    """Locate residual private paths without copying their values into logs."""
    locations: set[str] = set()
    for datablock in saved_blender_ids():
        sensitive_name = sensitive_path_value(datablock.name)
        safe_name = "<redacted-name>" if sensitive_name else datablock.name
        label = f"{datablock.bl_rna.identifier}:{safe_name}"
        if sensitive_name:
            locations.add(f"{label}.name")
        for key in list(datablock.keys()):
            sensitive_key = sensitive_path_value(key)
            if sensitive_key or value_contains_sensitive_path(datablock.get(key)):
                safe_key = "<redacted-key>" if sensitive_key else key
                locations.add(f"{label}[{safe_key}]")
        for attribute in ("filepath", "filepath_raw", "directory"):
            try:
                value = getattr(datablock, attribute)
            except (AttributeError, RuntimeError):
                continue
            if isinstance(value, str) and sensitive_path_value(value):
                locations.add(f"{label}.{attribute}")
        if isinstance(datablock, bpy.types.Text):
            try:
                if sensitive_path_value(datablock.as_string()):
                    locations.add(f"{label}.content")
            except RuntimeError:
                pass
    return sorted(locations)


def validate_private_paths() -> None:
    locations = sensitive_provenance_locations()
    if locations:
        raise BuildError(
            "Final character still contains private filesystem paths in: "
            + ", ".join(locations[:20])
            + (f" (+{len(locations) - 20} more)" if len(locations) > 20 else "")
        )
    log("private_path_validation_passed")


def approximately_equal(actual: float, expected: float, tolerance: float = 1.0e-5) -> bool:
    return math.isclose(actual, expected, rel_tol=tolerance, abs_tol=tolerance)


def matrix_maximum_delta(actual: Matrix, expected: Matrix) -> float:
    return max(
        abs(float(actual[row][column]) - float(expected[row][column]))
        for row in range(4)
        for column in range(4)
    )


def matrix_is_finite(matrix: Matrix) -> bool:
    return all(math.isfinite(float(value)) for row in matrix for value in row)


def validate_pose_control(
    scene: bpy.types.Scene,
    armatures: list[bpy.types.Object],
    captured_pose: dict[bpy.types.Object, dict[str, Matrix]],
    configured_report: dict[str, Any],
) -> dict[str, Any]:
    """Prove that the Timeline endpoints are exact and every sample is finite."""
    problems: list[str] = []
    expected_markers = {
        (POSE_REST_MARKER, POSE_REST_FRAME),
        (POSE_CAPTURED_MARKER, POSE_CAPTURED_FRAME),
    }
    actual_markers = {(marker.name, marker.frame) for marker in scene.timeline_markers}
    if actual_markers != expected_markers:
        problems.append(f"Timeline markers changed: {sorted(actual_markers)}")
    if (scene.frame_start, scene.frame_end) != (
        POSE_REST_FRAME,
        POSE_CAPTURED_FRAME,
    ):
        problems.append("Timeline pose-slider frame range changed")

    structural_fcurves = 0
    canonical_quaternion_w_curves = 0
    for armature in armatures:
        if armature.data.display_type != "STICK":
            problems.append(f"{armature.name} is not using Stick display")
        if armature.data.show_bone_custom_shapes:
            problems.append(f"{armature.name} still displays custom bone shapes")
        if armature.data.show_axes or armature.data.show_names:
            problems.append(f"{armature.name} still displays bone axes or names")
        assigned_shapes = [
            pose_bone.name
            for pose_bone in armature.pose.bones
            if pose_bone.custom_shape is not None
        ]
        if assigned_shapes:
            problems.append(
                f"{armature.name} retains {len(assigned_shapes)} custom bone shapes"
            )

        try:
            _, fcurves = action_fcurves_for_armature(armature)
        except BuildError as exc:
            problems.append(str(exc))
            continue
        structural_fcurves += len(fcurves)
        for fcurve in fcurves:
            points = list(fcurve.keyframe_points)
            frames = [float(point.co.x) for point in points]
            if (
                len(points) != 2
                or not approximately_equal(frames[0], POSE_REST_FRAME)
                or not approximately_equal(frames[1], POSE_CAPTURED_FRAME)
                or any(point.interpolation != "LINEAR" for point in points)
            ):
                problems.append(
                    f"Invalid pose-slider keys on {armature.name}: "
                    f"{fcurve.data_path}[{fcurve.array_index}]"
                )
                continue
            expected_rest = rest_channel_value(fcurve)
            if not approximately_equal(float(points[0].co.y), expected_rest):
                problems.append(
                    f"Non-rest frame-0 value on {armature.name}: "
                    f"{fcurve.data_path}[{fcurve.array_index}]"
                )
            if any(not math.isfinite(float(point.co.y)) for point in points):
                problems.append(
                    f"Non-finite pose-slider key on {armature.name}: "
                    f"{fcurve.data_path}[{fcurve.array_index}]"
                )
            if (
                fcurve.data_path.endswith(".rotation_quaternion")
                and fcurve.array_index == 0
            ):
                canonical_quaternion_w_curves += 1
                if float(points[1].co.y) < -POSE_MATRIX_TOLERANCE:
                    problems.append(
                        f"Captured quaternion is not canonical on {armature.name}: "
                        f"{fcurve.data_path}"
                    )

    identity = Matrix.Identity(4)
    rest_basis_delta = 0.0
    rest_pose_delta = 0.0
    captured_basis_delta = 0.0
    finite_values = 0
    try:
        for frame in POSE_SAMPLE_FRAMES:
            set_scene_frame(scene, frame)
            for armature in armatures:
                reference = captured_pose.get(armature)
                if reference is None:
                    problems.append(f"Missing captured reference for {armature.name}")
                    continue
                for pose_bone in armature.pose.bones:
                    if not matrix_is_finite(pose_bone.matrix_basis) or not matrix_is_finite(
                        pose_bone.matrix
                    ):
                        problems.append(
                            f"Non-finite pose transform at frame {frame}: "
                            f"{armature.name}/{pose_bone.name}"
                        )
                        continue
                    finite_values += 32
                    if frame == POSE_REST_FRAME:
                        rest_basis_delta = max(
                            rest_basis_delta,
                            matrix_maximum_delta(pose_bone.matrix_basis, identity),
                        )
                        rest_pose_delta = max(
                            rest_pose_delta,
                            matrix_maximum_delta(
                                pose_bone.matrix, pose_bone.bone.matrix_local
                            ),
                        )
                    elif frame == POSE_CAPTURED_FRAME:
                        captured_matrix = reference.get(pose_bone.name)
                        if captured_matrix is None:
                            problems.append(
                                f"Captured reference lost bone: "
                                f"{armature.name}/{pose_bone.name}"
                            )
                        else:
                            captured_basis_delta = max(
                                captured_basis_delta,
                                matrix_maximum_delta(
                                    pose_bone.matrix_basis, captured_matrix
                                ),
                            )
    finally:
        set_scene_frame(scene, POSE_CAPTURED_FRAME)

    if rest_basis_delta > POSE_MATRIX_TOLERANCE:
        problems.append(
            f"frame 0 does not reset pose bases (delta {rest_basis_delta:.9g})"
        )
    if rest_pose_delta > POSE_MATRIX_TOLERANCE:
        problems.append(
            f"frame 0 does not reproduce rig rest matrices (delta {rest_pose_delta:.9g})"
        )
    if captured_basis_delta > POSE_MATRIX_TOLERANCE:
        problems.append(
            "frame 100 does not reproduce captured pose matrices "
            f"(delta {captured_basis_delta:.9g})"
        )
    if structural_fcurves != configured_report["FCurves"]:
        problems.append("Pose-slider FCurve count changed after configuration")

    if problems:
        raise BuildError("Final pose-control validation failed: " + "; ".join(problems))

    return {
        **configured_report,
        "Markers": {
            POSE_REST_MARKER: POSE_REST_FRAME,
            POSE_CAPTURED_MARKER: POSE_CAPTURED_FRAME,
        },
        "DefaultFrame": scene.frame_current,
        "ArmaturesUsingStickDisplay": len(armatures),
        "CustomBoneShapeAssignments": 0,
        "CustomBoneShapeDisplay": False,
        "CanonicalQuaternionWCurvesValidated": canonical_quaternion_w_curves,
        "RestBasisMaximumDelta": rest_basis_delta,
        "RestPoseMaximumDelta": rest_pose_delta,
        "CapturedPoseMaximumDelta": captured_basis_delta,
        "FiniteMatrixValuesChecked": finite_values,
        "EndpointValidation": "passed",
    }


def validate_viewport_defaults(configured_report: dict[str, Any]) -> dict[str, Any]:
    problems: list[str] = []
    spaces = saved_view_3d_spaces()
    if len(spaces) != configured_report["SavedView3DSpaces"]:
        problems.append("saved 3D Viewport count changed")
    for label, space in spaces:
        overlay = space.overlay
        if not overlay.show_overlays:
            problems.append(f"{label} disabled all overlays, including bones")
        if overlay.show_extras:
            problems.append(f"{label} still shows camera/light extras")
        if overlay.show_relationship_lines:
            problems.append(f"{label} still shows relationship lines")
        if overlay.show_floor or overlay.show_ortho_grid:
            problems.append(f"{label} still shows the viewport grid")
        if overlay.show_axis_x or overlay.show_axis_y or overlay.show_axis_z:
            problems.append(f"{label} still shows coordinate axes")
        if not overlay.show_bones:
            problems.append(f"{label} hides bones")
        if not overlay.show_outline_selected:
            problems.append(f"{label} hides selection outlines")
    if problems:
        raise BuildError(
            "Final viewport-default validation failed: " + "; ".join(problems)
        )
    return {**configured_report, "Validation": "passed"}


def validate_scene_setup(
    collections: dict[str, bpy.types.Collection],
    meshes: list[bpy.types.Object],
) -> dict[str, Any]:
    """Require the removable studio to be complete and to frame the character."""
    scene = bpy.context.scene
    setup = collections["setup"]
    problems: list[str] = []
    _, validation_size, _ = character_bounds_across_frames(scene, meshes)
    validation_largest = max(max(validation_size), 0.1)

    if setup.name != SETUP_COLLECTION or not any(
        child == setup for child in scene.collection.children
    ):
        problems.append("Scene Setup is not linked directly beneath the scene")

    studio_cameras = [
        obj
        for obj in setup.objects
        if obj.get("xivblend_component") == "studio_camera"
    ]
    camera = studio_cameras[0] if len(studio_cameras) == 1 else None
    if len(studio_cameras) != 1:
        problems.append(f"expected 1 studio camera, found {len(studio_cameras)}")
    elif scene.camera is not camera:
        problems.append("the studio camera is not the active scene camera")
    elif (
        camera.type != "CAMERA"
        or camera.hide_render
        or camera.hide_viewport
        or camera.data.type != "PERSP"
        or not approximately_equal(camera.data.lens, 70.0)
        or camera.data.sensor_fit != "HORIZONTAL"
        or camera.data.dof.use_dof
        or camera.parent is not None
        or len(camera.constraints) != 0
    ):
        problems.append("the active studio camera is disabled or its portrait settings changed")

    studio_lights = [
        obj
        for obj in setup.objects
        if obj.get("xivblend_component") == "studio_light"
    ]
    if len(studio_lights) != 3:
        problems.append(f"expected 3 studio lights, found {len(studio_lights)}")
    expected_lights = {
        "key": (
            260.0, 0.78, 1.20, (1.0, 0.86, 0.76), math.radians(115.0)
        ),
        "fill": (
            48.0, 1.50, 1.85, (0.75, 0.82, 1.0), math.radians(125.0)
        ),
        "rim": (
            82.0, 0.28, 1.25, (0.65, 0.78, 1.0), math.radians(85.0)
        ),
    }
    if {
        str(light.get("xivblend_studio_role", "")) for light in studio_lights
    } != set(expected_lights):
        problems.append("the expected key, fill and rim lights were not found")
    for light in studio_lights:
        role = str(light.get("xivblend_studio_role", ""))
        expected = expected_lights.get(role)
        exact_settings_valid = False
        if expected is not None and light.type == "LIGHT":
            energy_scale, size_scale, size_y_scale, color, spread = expected
            expected_energy = energy_scale * validation_largest * validation_largest
            energy_tolerance = max(1.0e-5, expected_energy * 1.0e-5)
            exact_settings_valid = (
                approximately_equal(
                    light.data.energy, expected_energy, energy_tolerance
                )
                and approximately_equal(
                    light.data.size, size_scale * validation_largest
                )
                and approximately_equal(
                    light.data.size_y, size_y_scale * validation_largest
                )
                and approximately_equal(light.data.spread, spread)
                and all(
                    approximately_equal(actual, wanted)
                    for actual, wanted in zip(light.data.color, color)
                )
                and light.data.normalize
                and light.data.use_shadow
            )
        if (
            light.type != "LIGHT"
            or light.data.type != "AREA"
            or light.hide_render
            or light.hide_viewport
            or light.data.energy <= 0.0
            or light.data.shape != "RECTANGLE"
            or light.data.size <= 0.0
            or light.data.size_y <= 0.0
            or not exact_settings_valid
        ):
            problems.append(f"studio light {light.name} is disabled or its softbox settings changed")

    studio_backdrops = [
        obj
        for obj in setup.objects
        if obj.get("xivblend_component") == "studio_backdrop"
    ]
    if len(studio_backdrops) != 1:
        problems.append(f"expected 1 studio backdrop, found {len(studio_backdrops)}")
    else:
        backdrop = studio_backdrops[0]
        material = (
            backdrop.material_slots[0].material
            if backdrop.type == "MESH"
            and len(backdrop.material_slots) > 0
            else None
        )
        node_types = (
            {node.type for node in material.node_tree.nodes}
            if material is not None and material.node_tree is not None
            else set()
        )
        required_node_types = {
            "BSDF_PRINCIPLED",
            "EMISSION",
            "MIX_SHADER",
            "OUTPUT_MATERIAL",
            "TEX_COORD",
            "SEPXYZ",
            "VALTORGB",
            "MAP_RANGE",
        }
        has_surface_output = bool(
            material is not None
            and material.node_tree is not None
            and any(
                node.type == "OUTPUT_MATERIAL"
                and node.inputs.get("Surface") is not None
                and bool(node.inputs["Surface"].links)
                for node in material.node_tree.nodes
            )
        )
        if (
            backdrop.type != "MESH"
            or backdrop.hide_render
            or backdrop.hide_viewport
            or len(backdrop.data.polygons) < 500
            or len(backdrop.material_slots) == 0
            or any(slot.material is None for slot in backdrop.material_slots)
            or not required_node_types.issubset(node_types)
            or not has_surface_output
        ):
            problems.append("the studio backdrop is disabled or incomplete")

    expected_render_settings = (
        scene.render.engine == "BLENDER_EEVEE"
        and scene.render.resolution_x == 1440
        and scene.render.resolution_y == 1800
        and scene.render.resolution_percentage == 100
        and scene.render.image_settings.file_format == "PNG"
        and scene.render.image_settings.color_mode == "RGB"
        and scene.render.image_settings.color_depth == "16"
        and not scene.render.film_transparent
        and scene.view_settings.view_transform == "AgX"
        and scene.view_settings.look == "AgX - Medium High Contrast"
        and approximately_equal(scene.view_settings.exposure, -0.55)
        and scene.eevee.taa_render_samples == 96
        and scene.eevee.shadow_pool_size == "512"
        and scene.eevee.shadow_ray_count == 3
        and not scene.eevee.use_raytracing
        and scene.eevee.ray_tracing_method == "SCREEN"
        and scene.eevee.use_fast_gi
        and scene.eevee.fast_gi_method == "AMBIENT_OCCLUSION_ONLY"
        and approximately_equal(scene.eevee.fast_gi_quality, 0.75)
        and approximately_equal(
            scene.eevee.fast_gi_distance,
            max(validation_largest * 0.10, 0.05),
        )
    )
    if not expected_render_settings:
        problems.append("portrait render or AgX color-management settings changed")

    background = (
        scene.world.node_tree.nodes.get("Background")
        if scene.world is not None and scene.world.node_tree is not None
        else None
    )
    if (
        scene.world is None
        or scene.world.name != "Neutral World"
        or background is None
        or not all(
            approximately_equal(actual, expected)
            for actual, expected in zip(
                background.inputs["Color"].default_value,
                (0.004, 0.007, 0.014, 1.0),
            )
        )
        or not approximately_equal(background.inputs["Strength"].default_value, 0.035)
    ):
        problems.append("the neutral studio world is missing or changed")

    frame_min: list[float] | None = None
    frame_max: list[float] | None = None
    frame_reports: list[dict[str, Any]] = []
    if camera is not None and camera.type == "CAMERA":
        projected_across_frames = []
        try:
            for frame in STUDIO_CAMERA_SAMPLE_FRAMES:
                set_scene_frame(scene, frame)
                _, _, bounds = character_bounds(meshes)
                projected = [
                    world_to_camera_view(scene, camera, point) for point in bounds
                ]
                if not projected:
                    problems.append(
                        f"no visible character bounds were available at frame {frame}"
                    )
                    continue
                projected_across_frames.extend(projected)
                minimum = [
                    min(point[axis] for point in projected) for axis in range(3)
                ]
                maximum = [
                    max(point[axis] for point in projected) for axis in range(3)
                ]
                frame_reports.append(
                    {
                        "Frame": frame,
                        "Minimum": [round(value, 6) for value in minimum],
                        "Maximum": [round(value, 6) for value in maximum],
                    }
                )
                tolerance = 1.0e-4
                if any(
                    not all(math.isfinite(value) for value in point)
                    or point.x < -tolerance
                    or point.x > 1.0 + tolerance
                    or point.y < -tolerance
                    or point.y > 1.0 + tolerance
                    or point.z < camera.data.clip_start - tolerance
                    or point.z > camera.data.clip_end + tolerance
                    for point in projected
                ):
                    problems.append(
                        "the studio camera does not frame all visible character "
                        f"bounds at default render frame {frame}"
                    )
        finally:
            set_scene_frame(scene, POSE_CAPTURED_FRAME)

        if projected_across_frames:
            frame_min = [
                min(point[axis] for point in projected_across_frames)
                for axis in range(3)
            ]
            frame_max = [
                max(point[axis] for point in projected_across_frames)
                for axis in range(3)
            ]

    if problems:
        raise BuildError("Final Scene Setup validation failed: " + "; ".join(problems))

    return {
        "StudioCamera": camera.name if camera is not None else None,
        "StudioLights": len(studio_lights),
        "StudioBackdrops": len(studio_backdrops),
        "CameraFrameMinimum": [round(value, 6) for value in frame_min or []],
        "CameraFrameMaximum": [round(value, 6) for value in frame_max or []],
        "CameraFramingSamples": frame_reports,
        "CameraFramingFrames": list(STUDIO_CAMERA_SAMPLE_FRAMES),
        "RenderEngine": scene.render.engine,
    }


def placeholder_material_name(name: str) -> bool:
    return PLACEHOLDER_MATERIAL_RE.fullmatch(name.strip()) is not None


def validate_output(
    collections: dict[str, bpy.types.Collection],
    removed_vertex_groups: int,
    material_mapping: dict[str, Any],
    redacted_properties: int,
    captured_pose: dict[bpy.types.Object, dict[str, Matrix]],
    pose_configuration: dict[str, Any],
    viewport_configuration: dict[str, Any],
    primary_armature: bpy.types.Object,
) -> dict[str, Any]:
    meshes = [obj for obj in collections["meshes"].objects if obj.type == "MESH"]
    armatures = [obj for obj in collections["rig"].objects if obj.type == "ARMATURE"]
    armature_set = set(armatures)

    unbound = [
        obj.name
        for obj in meshes
        if not any(
            modifier.type == "ARMATURE" and modifier.object in armature_set
            for modifier in obj.modifiers
        )
    ]
    bad_materials: list[str] = []
    meshes_without_materials: list[str] = []
    for obj in meshes:
        if obj.hide_render or len(obj.data.polygons) == 0:
            continue
        if len(obj.material_slots) == 0:
            meshes_without_materials.append(obj.name)
            continue
        for slot_index, slot in enumerate(obj.material_slots):
            material = slot.material
            if material is None or placeholder_material_name(material.name):
                bad_materials.append(f"{obj.name}[{slot_index}]")

    unpacked = [
        image.name
        for image in bpy.data.images
        if image.source in {"FILE", "TILED"} and not image_is_packed(image)
    ]
    if unbound:
        raise BuildError("Final character meshes lost their rig binding: " + ", ".join(unbound))
    if meshes_without_materials:
        raise BuildError(
            "Visible character meshes contain no material slots: "
            + ", ".join(sorted(meshes_without_materials))
        )
    if bad_materials:
        raise BuildError("Final character meshes contain placeholder materials: " + ", ".join(bad_materials))
    if unpacked:
        raise BuildError("Final character images are not packed: " + ", ".join(unpacked))
    linked_datablocks = [
        f"{datablock.bl_rna.identifier}:{datablock.name}"
        for datablock in saved_blender_ids()
        if datablock.library is not None
    ]
    if linked_datablocks:
        raise BuildError(
            "Final character contains linked external Blender data: "
            + ", ".join(sorted(linked_datablocks))
        )

    expected_primary = max(
        armatures,
        key=lambda item: (len(item.data.bones), item.name.casefold()),
    )
    active_object = bpy.context.view_layer.objects.active
    if (
        primary_armature is not expected_primary
        or active_object is not expected_primary
        or not expected_primary.select_get()
        or expected_primary.mode != "OBJECT"
    ):
        raise BuildError(
            "Final Blender selection is not the largest armature in Object Mode"
        )

    # Appending the shader library can leave a zero-user Library ID even though
    # every material and node group was copied locally. It is safe to purge that
    # bookkeeping record once the linked-data check above has passed.
    for library in list(bpy.data.libraries):
        bpy.data.libraries.remove(library)

    pose_report = validate_pose_control(
        bpy.context.scene,
        armatures,
        captured_pose,
        pose_configuration,
    )
    setup_report = validate_scene_setup(collections, meshes)
    viewport_report = validate_viewport_defaults(viewport_configuration)

    return {
        "Builder": BUILDER_NAME,
        "BuilderVersion": BUILDER_VERSION,
        "Armatures": len(armatures),
        "Bones": sum(len(obj.data.bones) for obj in armatures),
        "CharacterMeshes": len(meshes),
        "Vertices": sum(len(obj.data.vertices) for obj in meshes),
        "Materials": len(bpy.data.materials),
        "SourceMaterials": material_mapping["source_materials"],
        "ExpectedMappedMaterials": material_mapping["expected"],
        "MappedMaterials": material_mapping["mapped"],
        "UnmappedMaterials": material_mapping["unmapped"],
        "Images": len(bpy.data.images),
        "PackedImages": sum(image_is_packed(image) for image in bpy.data.images),
        "EmptyVertexGroupsRemoved": removed_vertex_groups,
        "PrivatePropertiesRedacted": redacted_properties,
        "Actions": len(bpy.data.actions),
        "FrameRange": [bpy.context.scene.frame_start, bpy.context.scene.frame_end],
        "CurrentFrame": bpy.context.scene.frame_current,
        "PrimaryArmature": {
            "Name": primary_armature.name,
            "Bones": len(primary_armature.data.bones),
            "Mode": primary_armature.mode,
            "Selected": primary_armature.select_get(),
        },
        "PoseControl": pose_report,
        "ViewportDefaults": viewport_report,
        "SceneSetup": setup_report,
        "PrivatePathValidation": "passed",
        "Validation": "passed",
    }


def embed_build_report(scene: bpy.types.Scene, report: dict[str, Any]) -> None:
    text = bpy.data.texts.new(BUILD_REPORT_TEXT_NAME)
    text.write(json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True) + "\n")
    text.use_fake_user = True
    text["content_type"] = "application/json"
    scene["xivblend_build_report_text"] = BUILD_REPORT_TEXT_NAME
    scene["xivblend_validation"] = "passed"
    log("output_validated", **report)


def image_is_packed(image: bpy.types.Image) -> bool:
    try:
        if image.packed_file is not None:
            return True
    except (AttributeError, RuntimeError):
        pass
    try:
        return len(image.packed_files) > 0
    except (AttributeError, RuntimeError, TypeError):
        return False


def pack_resources() -> None:
    result = bpy.ops.file.pack_all()
    if "FINISHED" not in result:
        raise BuildError("Blender could not pack the character resources")

    unpacked: list[str] = []
    for image in bpy.data.images:
        if image.source not in {"FILE", "TILED"}:
            continue
        if image_is_packed(image):
            continue
        try:
            image.pack()
        except RuntimeError:
            unpacked.append(image.name)
            continue
        if not image_is_packed(image):
            unpacked.append(image.name)

    if unpacked:
        raise BuildError("Could not pack image resources: " + ", ".join(sorted(unpacked)))

    log("resources_packed", images=len(bpy.data.images))


def save_blend(output: Path) -> None:
    # Repeated exports should replace the requested artifact, not leave .blend1
    # backups beside it. The capture manifest is the build's provenance record.
    bpy.context.preferences.filepaths.save_version = 0
    result = bpy.ops.wm.save_as_mainfile(filepath=str(output), check_existing=False)
    if "FINISHED" not in result or not output.is_file() or output.stat().st_size == 0:
        raise BuildError(f"Blender did not create the output file: {output}")
    log("blend_saved", output=str(output), bytes=output.stat().st_size)


def main() -> None:
    if not bpy.app.background:
        raise BuildError("This builder must run with Blender's --background option")
    if bpy.app.version < (5, 0, 0) or bpy.app.version >= (6, 0, 0):
        raise BuildError("XivBlend currently supports Blender 5.x")

    arguments = parse_arguments()
    source, manifest_path, output = validate_paths(arguments)
    meddle_location = resolve_meddle_tools(arguments.meddle_tools)
    document, canonical, digest = read_manifest(manifest_path)

    log(
        "build_started",
        blender=bpy.app.version_string,
        input=source.name,
        manifest=manifest_path.name,
    )
    scene = clear_scene()
    imported, armatures, character_meshes = import_character(source)
    removed_vertex_groups = remove_empty_vertex_groups(character_meshes)
    captured_pose, pose_configuration = configure_armatures(scene, armatures)
    material_mapping = apply_meddle_materials(source, imported, meddle_location)
    collections = organize_objects(scene, imported)
    configure_scene_setup(scene, imported, collections["setup"])
    viewport_configuration = configure_viewport_defaults()
    embed_manifest(scene, manifest_path, document, canonical, digest, source)
    write_embedded_readme()
    material_mode = (
        "meddle-tools" if meddle_location is not None else "gltf"
    )
    scene["xivblend_material_mode"] = material_mode
    scene["xivblend_expected_mapped_materials"] = material_mapping["expected"]
    scene["xivblend_mapped_materials"] = material_mapping["mapped"]
    scene["xivblend_unmapped_materials"] = len(material_mapping["unmapped"])
    scene["clean_extract_material_mode"] = material_mode
    scene["clean_extract_mapped_materials"] = material_mapping["mapped"]
    pack_resources()
    redacted_properties = sanitize_packed_provenance(imported)
    primary_armature = select_primary_armature(armatures)
    apply_animation_identity(scene, primary_armature)
    report = validate_output(
        collections,
        removed_vertex_groups,
        material_mapping,
        redacted_properties,
        captured_pose,
        pose_configuration,
        viewport_configuration,
        primary_armature,
    )
    embed_build_report(scene, report)
    # Run the privacy assertion after the build report is embedded so every ID
    # that will actually be saved has been included in the recursive scan.
    validate_private_paths()
    set_scene_frame(scene, POSE_CAPTURED_FRAME)
    select_primary_armature(armatures)
    save_blend(output)


if __name__ == "__main__":
    try:
        main()
    except BuildError as exc:
        log("build_failed", error=str(exc))
        raise
