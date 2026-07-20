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
from mathutils import Vector


BUILDER_NAME = "XivBlend Blender Builder"
BUILDER_VERSION = "0.2.0"
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


def configure_armatures(
    scene: bpy.types.Scene, armatures: Iterable[bpy.types.Object]
) -> None:
    """Keep the original FFXIV rig data while making it pleasant to pose in Blender."""
    actions: set[bpy.types.Action] = set()
    for armature in armatures:
        armature.data.display_type = "STICK"
        armature.data.pose_position = "POSE"
        armature.show_in_front = True
        if armature.animation_data and armature.animation_data.action:
            actions.add(armature.animation_data.action)

    for index, action in enumerate(sorted(actions, key=lambda item: item.name)):
        action.name = (
            "XivBlend | Captured Pose"
            if index == 0
            else f"XivBlend | Captured Pose {index + 1}"
        )

    scene.frame_start = 0
    scene.frame_end = 0
    scene.frame_set(0)


def import_character(
    source: Path,
) -> tuple[list[bpy.types.Object], list[bpy.types.Object], list[bpy.types.Object]]:
    before = set(bpy.data.objects)
    result = bpy.ops.import_scene.gltf(filepath=str(source))
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

    custom_bone_shapes = bone_custom_shapes(imported)

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
        minimum = Vector((-0.5, -0.5, 0.0))
        maximum = Vector((0.5, 0.5, 2.0))
        points = [minimum, maximum]
        return (minimum + maximum) * 0.5, maximum - minimum, points

    minimum = Vector(tuple(min(point[axis] for point in points) for axis in range(3)))
    maximum = Vector(tuple(max(point[axis] for point in points) for axis in range(3)))
    return (minimum + maximum) * 0.5, maximum - minimum, points


def point_at(obj: bpy.types.Object, target: Vector) -> None:
    direction = target - obj.location
    if direction.length_squared > 0.0:
        obj.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()


def add_area_light(
    setup: bpy.types.Collection,
    name: str,
    location: Vector,
    target: Vector,
    energy: float,
    size: float,
    color: tuple[float, float, float],
) -> bpy.types.Object:
    light_data = bpy.data.lights.new(name, type="AREA")
    light_data.energy = energy
    light_data.color = color
    light_data.normalize = True
    light_data.shape = "DISK"
    light_data.size = size
    light = bpy.data.objects.new(name, light_data)
    setup.objects.link(light)
    light.location = location
    point_at(light, target)
    light["xivblend_component"] = "studio_light"
    return light


def create_studio_backdrop(
    setup: bpy.types.Collection,
    center: Vector,
    size: Vector,
    largest: float,
) -> bpy.types.Object:
    """Create a removable curved studio sweep beneath and behind the character."""
    floor_z = center.z - size.z * 0.5 - max(largest * 0.002, 0.001)
    half_width = largest * 5.0
    curve_steps = 12
    width_steps = 16
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
        emission = nodes.new("ShaderNodeEmission")
        coordinates = nodes.new("ShaderNodeTexCoord")
        separate = nodes.new("ShaderNodeSeparateXYZ")
        color_ramp = nodes.new("ShaderNodeValToRGB")
        color_ramp.color_ramp.elements[0].position = 0.0
        color_ramp.color_ramp.elements[0].color = (0.055, 0.075, 0.12, 1.0)
        color_ramp.color_ramp.elements[1].position = 1.0
        color_ramp.color_ramp.elements[1].color = (0.012, 0.018, 0.035, 1.0)
        transition = nodes.new("ShaderNodeMapRange")
        transition.clamp = True
        transition.inputs["From Min"].default_value = 0.015
        transition.inputs["From Max"].default_value = 0.17
        transition.inputs["To Min"].default_value = 0.0
        transition.inputs["To Max"].default_value = 1.0
        mix = nodes.new("ShaderNodeMixShader")
        emission.inputs["Strength"].default_value = 1.0
        links.new(coordinates.outputs["Generated"], separate.inputs["Vector"])
        links.new(separate.outputs["Z"], color_ramp.inputs["Fac"])
        links.new(separate.outputs["Z"], transition.inputs["Value"])
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
    scene.render.resolution_x = 1080
    scene.render.resolution_y = 1350
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.color_depth = "8"
    scene.render.film_transparent = False
    try:
        scene.view_settings.view_transform = "AgX"
        scene.view_settings.look = "AgX - Medium High Contrast"
    except TypeError:
        # Keep Blender's default transform when a future build renames the view.
        pass
    scene.view_settings.exposure = -0.35
    scene.view_settings.gamma = 1.0

    world = bpy.data.worlds.new("Neutral World")
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", DeprecationWarning)
        world.use_nodes = True
    background = world.node_tree.nodes.get("Background") if world.node_tree else None
    if background is not None:
        background.inputs["Color"].default_value = (0.018, 0.024, 0.038, 1.0)
        background.inputs["Strength"].default_value = 0.18
    scene.world = world

    center, size, bounds = character_bounds(imported)
    largest = max(max(size), 0.1)

    # Always create an unparented camera so an imported camera's hidden state,
    # constraints or parent transform cannot compromise deterministic framing.
    for source_camera in (obj for obj in imported if obj.type == "CAMERA"):
        source_camera.hide_render = True
        source_camera.hide_viewport = True
    camera_data = bpy.data.cameras.new("XivBlend Studio Camera")
    camera = bpy.data.objects.new("XivBlend Studio Camera", camera_data)
    setup.objects.link(camera)
    camera.data.lens = 55.0
    camera.data.sensor_fit = "HORIZONTAL"
    camera.data.dof.use_dof = False
    camera.hide_render = False
    camera.hide_viewport = False
    camera_target = center + Vector((0.0, 0.0, size.z * 0.02))
    render_aspect = (
        scene.render.resolution_x
        * scene.render.pixel_aspect_x
        / max(scene.render.resolution_y * scene.render.pixel_aspect_y, 1.0e-6)
    )
    tan_half_x = max(math.tan(camera.data.angle_x * 0.5), 1.0e-4)
    tan_half_y = tan_half_x / max(render_aspect, 1.0e-4)
    fit_margin = 1.12
    camera_distance = max(
        max(
            abs(point.x - camera_target.x) * fit_margin / tan_half_x
            - (point.y - camera_target.y),
            abs(point.z - camera_target.z) * fit_margin / tan_half_y
            - (point.y - camera_target.y),
        )
        for point in bounds
    )
    camera_distance = max(camera_distance, largest * 0.75)
    camera.location = camera_target + Vector((0.0, -camera_distance, 0.0))
    camera.data.clip_start = max(camera_distance / 1000.0, 0.001)
    camera.data.clip_end = max(camera_distance + largest * 20.0, 100.0)
    point_at(camera, camera_target)
    camera["xivblend_component"] = "studio_camera"
    scene.camera = camera

    # Disable any glTF lights so every export gets the same predictable studio rig.
    for source_light in (obj for obj in imported if obj.type == "LIGHT"):
        source_light.hide_render = True
        source_light.hide_viewport = True

    light_target = center + Vector((0.0, 0.0, size.z * 0.14))
    add_area_light(
        setup,
        "Key Light (Warm Softbox)",
        center + Vector((-largest * 1.25, -largest * 1.45, largest * 1.45)),
        light_target,
        430.0 * largest * largest,
        largest * 1.20,
        (1.0, 0.88, 0.78),
    )
    add_area_light(
        setup,
        "Fill Light (Cool Softbox)",
        center + Vector((largest * 1.35, -largest * 0.75, largest * 0.65)),
        light_target,
        90.0 * largest * largest,
        largest * 1.75,
        (0.58, 0.72, 1.0),
    )
    add_area_light(
        setup,
        "Rim Light",
        center + Vector((largest * 0.75, largest * 1.15, largest * 1.55)),
        center + Vector((0.0, 0.0, size.z * 0.22)),
        300.0 * largest * largest,
        largest * 0.90,
        (0.62, 0.78, 1.0),
    )
    create_studio_backdrop(setup, center, size, largest)
    bpy.context.view_layer.update()


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
    summary = {
        "SchemaVersion": document.get(
            "SchemaVersion", document.get("schemaVersion", document.get("schema_version"))
        ),
        "CapturedAtUtc": document.get(
            "CapturedAtUtc", document.get("capturedAtUtc", document.get("captured_at_utc"))
        ),
        "Source": document.get("Source", document.get("source")),
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


def write_embedded_readme() -> None:
    text = bpy.data.texts.new(README_TEXT_NAME)
    text.write(
        "XivBlend character export\n"
        "=========================\n\n"
        "- The Rig collection contains the original FFXIV bone hierarchy, names, "
        "rest transforms, pose and deformation weights reconstructed through glTF.\n"
        "- The captured one-frame pose is at frame 0. Set the armature to Rest Position "
        "to inspect the rest skeleton.\n"
        "- Press Numpad 0 for the generated camera and F12 to render.\n"
        "- The portrait camera, three lights and studio sweep are isolated in the "
        "Scene Setup collection and can be hidden or replaced without touching the character.\n"
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


def validate_scene_setup(
    collections: dict[str, bpy.types.Collection],
    meshes: list[bpy.types.Object],
) -> dict[str, Any]:
    """Require the removable studio to be complete and to frame the character."""
    scene = bpy.context.scene
    setup = collections["setup"]
    problems: list[str] = []

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
    ):
        problems.append("the active studio camera is disabled or not perspective")

    studio_lights = [
        obj
        for obj in setup.objects
        if obj.get("xivblend_component") == "studio_light"
    ]
    if len(studio_lights) != 3:
        problems.append(f"expected 3 studio lights, found {len(studio_lights)}")
    for light in studio_lights:
        if (
            light.type != "LIGHT"
            or light.data.type != "AREA"
            or light.hide_render
            or light.hide_viewport
            or light.data.energy <= 0.0
            or light.data.size <= 0.0
        ):
            problems.append(f"studio light {light.name} is disabled or not a usable AREA light")

    studio_backdrops = [
        obj
        for obj in setup.objects
        if obj.get("xivblend_component") == "studio_backdrop"
    ]
    if len(studio_backdrops) != 1:
        problems.append(f"expected 1 studio backdrop, found {len(studio_backdrops)}")
    else:
        backdrop = studio_backdrops[0]
        if (
            backdrop.type != "MESH"
            or backdrop.hide_render
            or backdrop.hide_viewport
            or len(backdrop.data.polygons) == 0
            or len(backdrop.material_slots) == 0
            or any(slot.material is None for slot in backdrop.material_slots)
        ):
            problems.append("the studio backdrop is disabled or incomplete")

    expected_render_settings = (
        scene.render.engine == "BLENDER_EEVEE"
        and scene.render.resolution_x == 1080
        and scene.render.resolution_y == 1350
        and scene.render.resolution_percentage == 100
        and scene.render.image_settings.file_format == "PNG"
        and scene.render.image_settings.color_mode == "RGBA"
        and scene.render.image_settings.color_depth == "8"
        and not scene.render.film_transparent
        and scene.view_settings.view_transform == "AgX"
        and scene.view_settings.look == "AgX - Medium High Contrast"
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
                (0.018, 0.024, 0.038, 1.0),
            )
        )
        or not approximately_equal(background.inputs["Strength"].default_value, 0.18)
    ):
        problems.append("the neutral studio world is missing or changed")

    frame_min: list[float] | None = None
    frame_max: list[float] | None = None
    if camera is not None and camera.type == "CAMERA":
        _, _, bounds = character_bounds(meshes)
        projected = [world_to_camera_view(scene, camera, point) for point in bounds]
        if projected:
            frame_min = [min(point[axis] for point in projected) for axis in range(3)]
            frame_max = [max(point[axis] for point in projected) for axis in range(3)]
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
                problems.append("the studio camera does not frame all visible character bounds")
        else:
            problems.append("no visible character bounds were available for camera validation")

    if problems:
        raise BuildError("Final Scene Setup validation failed: " + "; ".join(problems))

    return {
        "StudioCamera": camera.name if camera is not None else None,
        "StudioLights": len(studio_lights),
        "StudioBackdrops": len(studio_backdrops),
        "CameraFrameMinimum": [round(value, 6) for value in frame_min or []],
        "CameraFrameMaximum": [round(value, 6) for value in frame_max or []],
        "RenderEngine": scene.render.engine,
    }


def placeholder_material_name(name: str) -> bool:
    return PLACEHOLDER_MATERIAL_RE.fullmatch(name.strip()) is not None


def validate_output(
    collections: dict[str, bpy.types.Collection],
    removed_vertex_groups: int,
    material_mapping: dict[str, Any],
    redacted_properties: int,
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

    # Appending the shader library can leave a zero-user Library ID even though
    # every material and node group was copied locally. It is safe to purge that
    # bookkeeping record once the linked-data check above has passed.
    for library in list(bpy.data.libraries):
        bpy.data.libraries.remove(library)

    setup_report = validate_scene_setup(collections, meshes)

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
    configure_armatures(scene, armatures)
    material_mapping = apply_meddle_materials(source, imported, meddle_location)
    collections = organize_objects(scene, imported)
    configure_scene_setup(scene, imported, collections["setup"])
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
    report = validate_output(
        collections,
        removed_vertex_groups,
        material_mapping,
        redacted_properties,
    )
    embed_build_report(scene, report)
    # Run the privacy assertion after the build report is embedded so every ID
    # that will actually be saved has been included in the recursive scan.
    validate_private_paths()
    save_blend(output)


if __name__ == "__main__":
    try:
        main()
    except BuildError as exc:
        log("build_failed", error=str(exc))
        raise
