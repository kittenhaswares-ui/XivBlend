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
import os
from pathlib import Path
import sys
from typing import Any, Iterable

import bpy
from mathutils import Vector


BUILDER_NAME = "Clean Extract Blender Builder"
BUILDER_VERSION = "0.1.0"
MANIFEST_TEXT_NAME = "FFXIV_SNAPSHOT_MANIFEST.json"
MAX_MANIFEST_BYTES = 16 * 1024 * 1024

CHARACTER_COLLECTION = "FFXIV Character"
RIG_COLLECTION = "Rig"
MESH_COLLECTION = "Meshes"
EXTRAS_COLLECTION = "Character Extras"
SETUP_COLLECTION = "Scene Setup"


class BuildError(RuntimeError):
    """A user-actionable build failure."""


def log(event: str, **details: Any) -> None:
    payload = {"event": event, **details}
    print(f"[clean-extract] {json.dumps(payload, ensure_ascii=False, sort_keys=True)}", flush=True)


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
    scene.frame_start = 1
    scene.frame_end = 1
    scene.frame_set(1)
    return scene


def validate_character_rig(
    imported: Iterable[bpy.types.Object],
) -> tuple[list[bpy.types.Object], list[bpy.types.Object]]:
    """Require an imported armature with at least one mesh actually bound to it."""
    imported = list(imported)
    armatures = [obj for obj in imported if obj.type == "ARMATURE"]
    if not armatures:
        raise BuildError("The glTF file did not contain a character armature")

    armature_set = set(armatures)
    bound_meshes = [
        obj
        for obj in imported
        if obj.type == "MESH"
        and any(
            modifier.type == "ARMATURE" and modifier.object in armature_set
            for modifier in obj.modifiers
        )
    ]
    if not bound_meshes:
        raise BuildError(
            "The glTF file did not contain a character mesh bound to its imported armature"
        )

    return armatures, bound_meshes


def import_character(source: Path) -> list[bpy.types.Object]:
    before = set(bpy.data.objects)
    result = bpy.ops.import_scene.gltf(filepath=str(source))
    if "FINISHED" not in result:
        raise BuildError(f"Blender could not import {source.name}")

    imported = [obj for obj in bpy.data.objects if obj not in before]
    if not imported:
        raise BuildError("The glTF file did not contain any importable objects")
    armatures, bound_meshes = validate_character_rig(imported)

    log(
        "model_imported",
        file=source.name,
        objects=len(imported),
        meshes=sum(obj.type == "MESH" for obj in imported),
        armatures=len(armatures),
        bound_meshes=len(bound_meshes),
    )
    return imported


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
) -> int:
    if meddle_location is None:
        return 0

    package, package_parent = meddle_location
    sys.path.insert(0, str(package_parent))
    try:
        from MeddleTools import blend_import, version
        from MeddleTools.node_setup import node_configs

        # Calling the two data functions directly avoids UI operators, extension
        # installation, preferences, timers, and MeddleTools' network version check.
        version.updateCurrentRelease()
        blend_import.import_shaders()

        material_slots: dict[bpy.types.Material, list[bpy.types.MaterialSlot]] = {}
        for obj in imported:
            if obj.type != "MESH":
                continue
            for slot in obj.material_slots:
                if slot.material is not None:
                    material_slots.setdefault(slot.material, []).append(slot)

        applied = 0
        cache_directory = str(source.parent / "cache")
        for material, slots in material_slots.items():
            node_configs.map_mesh(material, slots, cache_directory)
            if any(slot.material is not material for slot in slots):
                applied += 1

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
            mapped_materials=applied,
        )
        return applied
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


def character_bounds(objects: Iterable[bpy.types.Object]) -> tuple[Vector, Vector]:
    objects = list(objects)
    custom_shapes = bone_custom_shapes(objects)
    points: list[Vector] = []
    for obj in objects:
        if obj.type != "MESH" or obj in custom_shapes or not obj.bound_box:
            continue
        points.extend(obj.matrix_world @ Vector(corner) for corner in obj.bound_box)

    if not points:
        return Vector((0.0, 0.0, 1.0)), Vector((1.0, 1.0, 2.0))

    minimum = Vector(tuple(min(point[axis] for point in points) for axis in range(3)))
    maximum = Vector(tuple(max(point[axis] for point in points) for axis in range(3)))
    return (minimum + maximum) * 0.5, maximum - minimum


def point_at(obj: bpy.types.Object, target: Vector) -> None:
    direction = target - obj.location
    if direction.length_squared > 0.0:
        obj.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()


def configure_scene_setup(
    scene: bpy.types.Scene,
    imported: list[bpy.types.Object],
    setup: bpy.types.Collection,
) -> None:
    world = bpy.data.worlds.new("Neutral World")
    world.use_nodes = True
    background = world.node_tree.nodes.get("Background") if world.node_tree else None
    if background is not None:
        background.inputs["Color"].default_value = (0.055, 0.055, 0.055, 1.0)
        background.inputs["Strength"].default_value = 0.6
    scene.world = world

    center, size = character_bounds(imported)
    largest = max(max(size), 1.0)

    cameras = [obj for obj in imported if obj.type == "CAMERA"]
    if cameras:
        camera = cameras[0]
    else:
        camera_data = bpy.data.cameras.new("Camera")
        camera = bpy.data.objects.new("Camera", camera_data)
        setup.objects.link(camera)
        camera.location = center + Vector((0.0, -largest * 2.8, size.z * 0.08))
        camera.data.lens = 55.0
        camera.data.clip_start = max(largest / 1000.0, 0.01)
        camera.data.clip_end = max(largest * 100.0, 100.0)
        point_at(camera, center)
    scene.camera = camera

    lights = [obj for obj in imported if obj.type == "LIGHT"]
    if not lights:
        light_data = bpy.data.lights.new("Key Light", type="AREA")
        light_data.energy = 700.0 * largest * largest
        light_data.shape = "DISK"
        light_data.size = largest * 2.0
        light = bpy.data.objects.new("Key Light", light_data)
        setup.objects.link(light)
        light.location = center + Vector((largest * 1.5, -largest * 1.8, largest * 2.2))
        point_at(light, center)

    scene.render.resolution_x = 1024
    scene.render.resolution_y = 1024
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.film_transparent = False


def embed_manifest(
    scene: bpy.types.Scene,
    manifest_path: Path,
    document: dict[str, Any],
    canonical: str,
    digest: str,
    source: Path,
) -> None:
    # A parsed JSON text block is inert data. In particular, never use eval/exec
    # and never give the block a .py name, even when manifest values are hostile.
    text = bpy.data.texts.new(MANIFEST_TEXT_NAME)
    text.write(canonical)
    text.use_fake_user = True
    text["content_type"] = "application/json"
    text["sha256"] = digest
    text["source_filename"] = manifest_path.name

    scene["clean_extract_builder"] = BUILDER_NAME
    scene["clean_extract_builder_version"] = BUILDER_VERSION
    scene["clean_extract_manifest_text"] = MANIFEST_TEXT_NAME
    scene["clean_extract_manifest_sha256"] = digest
    scene["clean_extract_manifest_filename"] = manifest_path.name
    scene["clean_extract_source_filename"] = source.name

    schema_value = document.get("schemaVersion", document.get("schema_version"))
    if isinstance(schema_value, (str, int, float)) and not isinstance(schema_value, bool):
        scene["clean_extract_snapshot_schema"] = str(schema_value)[:128]


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
    if bpy.app.version < (5, 0, 0):
        raise BuildError("Blender 5.0 or newer is required")

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
    imported = import_character(source)
    mapped_materials = apply_meddle_materials(source, imported, meddle_location)
    collections = organize_objects(scene, imported)
    configure_scene_setup(scene, imported, collections["setup"])
    embed_manifest(scene, manifest_path, document, canonical, digest, source)
    scene["clean_extract_material_mode"] = (
        "meddle-tools" if meddle_location is not None else "gltf"
    )
    scene["clean_extract_mapped_materials"] = mapped_materials
    pack_resources()
    save_blend(output)


if __name__ == "__main__":
    try:
        main()
    except BuildError as exc:
        log("build_failed", error=str(exc))
        raise
