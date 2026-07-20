"""Strict, read-only health audit for an opened XivBlend ``.blend`` file.

Run with Blender, for example::

    blender --background character.blend --python tools/audit_blend.py

The default is intentionally CI-friendly: the script prints one JSON report and
exits nonzero when strict issues are present.  Use ``-- --report-only`` to keep
the strict pass/fail result in JSON while forcing a zero process exit code::

    blender --background character.blend --python tools/audit_blend.py -- --report-only

The script never changes or saves the opened file.
"""

from __future__ import annotations

import argparse
from collections import Counter
import json
import math
import os
from pathlib import Path
import re
import sys
from typing import Any, Iterable

import bpy
from bpy_extras.object_utils import world_to_camera_view
from mathutils import Vector


RIG_COLLECTION = "Rig"
MESH_COLLECTION = "Meshes"
SETUP_COLLECTION = "Scene Setup"

STUDIO_CAMERA_TAG = "studio_camera"
STUDIO_LIGHT_TAG = "studio_light"
STUDIO_BACKDROP_TAG = "studio_backdrop"
COMPONENT_PROPERTY = "xivblend_component"

EXPECTED_STUDIO_LIGHTS = 3
EXPECTED_RESOLUTION = (1080, 1350, 100)
PLACEHOLDER_MATERIAL = re.compile(r"^(?:null|error)(?:\.\d+)?$", re.IGNORECASE)
WEIGHT_EPSILON = 1.0e-8
FLOAT_EPSILON = 1.0e-5


def arguments() -> argparse.Namespace:
    separator = sys.argv.index("--") if "--" in sys.argv else len(sys.argv)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--report-only",
        action="store_true",
        help="Print strict failures but always return process exit code zero.",
    )
    return parser.parse_args(sys.argv[separator + 1 :])


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


def material_render_method(material: bpy.types.Material) -> str | None:
    for attribute in ("surface_render_method", "blend_method"):
        try:
            value = getattr(material, attribute)
        except AttributeError:
            continue
        if value is not None:
            return str(value)
    return None


def is_placeholder_material(material: bpy.types.Material | None) -> bool:
    return material is None or PLACEHOLDER_MATERIAL.fullmatch(material.name.strip()) is not None


def bone_custom_shapes(armatures: Iterable[bpy.types.Object]) -> set[bpy.types.Object]:
    return {
        pose_bone.custom_shape
        for armature in armatures
        if armature.pose is not None
        for pose_bone in armature.pose.bones
        if pose_bone.custom_shape is not None
    }


def collection_objects(
    collection: bpy.types.Collection | None,
) -> set[bpy.types.Object]:
    return set(collection.objects) if collection is not None else set()


def audit_mesh(
    obj: bpy.types.Object,
    intended_rigs: set[bpy.types.Object],
    is_character_mesh: bool,
    is_custom_bone_shape: bool,
    is_scene_setup: bool,
) -> dict[str, Any]:
    mesh = obj.data
    armature_modifiers = [
        modifier for modifier in obj.modifiers if modifier.type == "ARMATURE"
    ]
    valid_modifiers = [
        modifier
        for modifier in armature_modifiers
        if modifier.object is not None and modifier.object in intended_rigs
    ]
    invalid_modifiers = [
        {
            "name": modifier.name,
            "target": modifier.object.name if modifier.object is not None else None,
        }
        for modifier in armature_modifiers
        if modifier.object is None or modifier.object not in intended_rigs
    ]

    valid_bone_names = {
        bone.name
        for modifier in valid_modifiers
        for bone in modifier.object.data.bones
    }
    group_names = {group.index: group.name for group in obj.vertex_groups}
    weighted_group_indices: set[int] = set()
    vertices_without_positive_weights = 0
    vertices_without_valid_rig_weights = 0
    invalid_positive_assignments = 0
    max_influences = 0

    for vertex in mesh.vertices:
        positive = [
            assignment
            for assignment in vertex.groups
            if assignment.weight > WEIGHT_EPSILON
        ]
        max_influences = max(max_influences, len(positive))
        weighted_group_indices.update(assignment.group for assignment in positive)
        if not positive:
            vertices_without_positive_weights += 1
        valid = [
            assignment
            for assignment in positive
            if group_names.get(assignment.group) in valid_bone_names
        ]
        invalid_positive_assignments += len(positive) - len(valid)
        if not valid:
            vertices_without_valid_rig_weights += 1

    placeholder_slots = [
        {
            "slot": index,
            "material": slot.material.name if slot.material is not None else None,
        }
        for index, slot in enumerate(obj.material_slots)
        if is_placeholder_material(slot.material)
    ]

    return {
        "name": obj.name,
        "is_character_mesh": is_character_mesh,
        "is_custom_bone_shape": is_custom_bone_shape,
        "is_scene_setup": is_scene_setup,
        "vertices": len(mesh.vertices),
        "polygons": len(mesh.polygons),
        "uv_layers": len(mesh.uv_layers),
        "color_attributes": len(mesh.color_attributes),
        "material_slots": len(obj.material_slots),
        "zero_material_slots": len(obj.material_slots) == 0,
        "placeholder_material_slots": placeholder_slots,
        "vertex_groups": len(obj.vertex_groups),
        "weighted_vertex_groups": len(weighted_group_indices),
        "empty_vertex_groups": len(obj.vertex_groups) - len(weighted_group_indices),
        "vertices_without_positive_weights": vertices_without_positive_weights,
        "vertices_without_valid_rig_weights": vertices_without_valid_rig_weights,
        "invalid_positive_weight_assignments": invalid_positive_assignments,
        "max_influences": max_influences,
        "armature_modifiers": [
            {
                "name": modifier.name,
                "target": modifier.object.name if modifier.object is not None else None,
                "intended_rig": modifier in valid_modifiers,
            }
            for modifier in armature_modifiers
        ],
        "valid_intended_rig_modifiers": len(valid_modifiers),
        "invalid_armature_modifiers": invalid_modifiers,
        "shape_keys": (
            len(mesh.shape_keys.key_blocks)
            if mesh.shape_keys is not None
            else 0
        ),
        "hide_render": obj.hide_render,
        "world_determinant": round(obj.matrix_world.determinant(), 8),
    }


def audit_armature(obj: bpy.types.Object) -> dict[str, Any]:
    armature = obj.data
    bones = list(armature.bones)
    return {
        "name": obj.name,
        "bones": len(bones),
        "deform_bones": sum(bone.use_deform for bone in bones),
        "root_bones": [bone.name for bone in bones if bone.parent is None],
        "zero_length_bones": [bone.name for bone in bones if bone.length <= 1.0e-7],
        "display_type": armature.display_type,
        "pose_position": armature.pose_position,
        "show_in_front": obj.show_in_front,
        "world_determinant": round(obj.matrix_world.determinant(), 8),
    }


def audit_material(material: bpy.types.Material) -> dict[str, Any]:
    nodes = list(material.node_tree.nodes) if material.node_tree else []
    image_nodes = [node for node in nodes if node.type == "TEX_IMAGE"]
    group_nodes = [node for node in nodes if node.type == "GROUP"]
    return {
        "name": material.name,
        "users": material.users,
        "placeholder_name": is_placeholder_material(material),
        "use_nodes": material.node_tree is not None,
        "nodes": len(nodes),
        "render_method": material_render_method(material),
        "image_nodes": len(image_nodes),
        "empty_optional_image_nodes": sum(node.image is None for node in image_nodes),
        "empty_optional_image_node_names": sorted(
            node.name for node in image_nodes if node.image is None
        ),
        "groups": sorted(
            {
                node.node_tree.name
                for node in group_nodes
                if node.node_tree is not None
            }
        ),
    }


def audit_image(image: bpy.types.Image) -> dict[str, Any]:
    packed = image_is_packed(image)
    path = bpy.path.abspath(image.filepath, library=image.library) if image.filepath else ""
    external_missing = bool(
        image.source in {"FILE", "TILED"}
        and not packed
        and path
        and not os.path.exists(path)
    )
    return {
        "name": image.name,
        "source": image.source,
        "size": list(image.size),
        "packed": packed,
        "external_missing": external_missing,
        "colorspace": image.colorspace_settings.name,
        "users": image.users,
    }


def evaluated_bounds(objects: Iterable[bpy.types.Object]) -> list[Vector]:
    depsgraph = bpy.context.evaluated_depsgraph_get()
    points: list[Vector] = []
    for obj in objects:
        evaluated = obj.evaluated_get(depsgraph)
        if not evaluated.bound_box:
            continue
        points.extend(
            evaluated.matrix_world @ Vector(corner)
            for corner in evaluated.bound_box
        )
    return points


def camera_framing(
    scene: bpy.types.Scene,
    camera: bpy.types.Object | None,
    character_meshes: Iterable[bpy.types.Object],
) -> dict[str, Any]:
    points = evaluated_bounds(character_meshes)
    if camera is None or camera.type != "CAMERA" or not points:
        return {
            "checked": False,
            "all_bounds_in_frame": False,
            "reason": "active camera or character bounds unavailable",
        }

    projected = [world_to_camera_view(scene, camera, point) for point in points]
    finite = all(
        math.isfinite(value)
        for point in projected
        for value in (point.x, point.y, point.z)
    )
    x_range = [min(point.x for point in projected), max(point.x for point in projected)]
    y_range = [min(point.y for point in projected), max(point.y for point in projected)]
    z_range = [min(point.z for point in projected), max(point.z for point in projected)]
    margin = 1.0e-4
    in_frame = finite and all(
        -margin <= point.x <= 1.0 + margin
        and -margin <= point.y <= 1.0 + margin
        and camera.data.clip_start - margin <= point.z <= camera.data.clip_end + margin
        for point in projected
    )
    return {
        "checked": True,
        "points": len(projected),
        "finite": finite,
        "x_range": [round(value, 6) for value in x_range],
        "y_range": [round(value, 6) for value in y_range],
        "z_range": [round(value, 6) for value in z_range],
        "all_bounds_in_frame": in_frame,
    }


def audit_studio(
    scene: bpy.types.Scene,
    setup_collection: bpy.types.Collection | None,
    character_meshes: list[bpy.types.Object],
) -> dict[str, Any]:
    setup_objects = collection_objects(setup_collection)
    tagged_cameras = [
        obj
        for obj in bpy.data.objects
        if obj.get(COMPONENT_PROPERTY) == STUDIO_CAMERA_TAG
    ]
    tagged_lights = [
        obj
        for obj in bpy.data.objects
        if obj.get(COMPONENT_PROPERTY) == STUDIO_LIGHT_TAG
    ]
    tagged_backdrops = [
        obj
        for obj in bpy.data.objects
        if obj.get(COMPONENT_PROPERTY) == STUDIO_BACKDROP_TAG
    ]
    enabled_lights = [
        obj
        for obj in bpy.data.objects
        if obj.type == "LIGHT" and not obj.hide_render
    ]

    active_camera = scene.camera
    framing = camera_framing(scene, active_camera, character_meshes)
    render = scene.render
    image_settings = render.image_settings
    render_checks = {
        "engine": {
            "actual": render.engine,
            "expected": "BLENDER_EEVEE",
            "valid": render.engine == "BLENDER_EEVEE",
        },
        "resolution": {
            "actual": [render.resolution_x, render.resolution_y, render.resolution_percentage],
            "expected": list(EXPECTED_RESOLUTION),
            "valid": (
                render.resolution_x,
                render.resolution_y,
                render.resolution_percentage,
            ) == EXPECTED_RESOLUTION,
        },
        "pixel_aspect": {
            "actual": [render.pixel_aspect_x, render.pixel_aspect_y],
            "expected": [1.0, 1.0],
            "valid": abs(render.pixel_aspect_x - 1.0) <= FLOAT_EPSILON
            and abs(render.pixel_aspect_y - 1.0) <= FLOAT_EPSILON,
        },
        "image_format": {
            "actual": image_settings.file_format,
            "expected": "PNG",
            "valid": image_settings.file_format == "PNG",
        },
        "color_mode": {
            "actual": image_settings.color_mode,
            "expected": "RGBA",
            "valid": image_settings.color_mode == "RGBA",
        },
        "color_depth": {
            "actual": image_settings.color_depth,
            "expected": "8",
            "valid": image_settings.color_depth == "8",
        },
        "film_transparent": {
            "actual": render.film_transparent,
            "expected": False,
            "valid": render.film_transparent is False,
        },
        "view_transform": {
            "actual": scene.view_settings.view_transform,
            "expected": "AgX",
            "valid": scene.view_settings.view_transform == "AgX",
        },
        "look": {
            "actual": scene.view_settings.look,
            "expected": "AgX - Medium High Contrast",
            "valid": scene.view_settings.look == "AgX - Medium High Contrast",
        },
        "exposure": {
            "actual": scene.view_settings.exposure,
            "expected": -0.35,
            "valid": abs(scene.view_settings.exposure - (-0.35)) <= FLOAT_EPSILON,
        },
        "gamma": {
            "actual": scene.view_settings.gamma,
            "expected": 1.0,
            "valid": abs(scene.view_settings.gamma - 1.0) <= FLOAT_EPSILON,
        },
        "frame_range": {
            "actual": [scene.frame_start, scene.frame_end],
            "expected": [0, 0],
            "valid": [scene.frame_start, scene.frame_end] == [0, 0],
        },
        "units": {
            "actual": [
                scene.unit_settings.system,
                scene.unit_settings.scale_length,
                scene.unit_settings.length_unit,
            ],
            "expected": ["METRIC", 1.0, "METERS"],
            "valid": scene.unit_settings.system == "METRIC"
            and abs(scene.unit_settings.scale_length - 1.0) <= FLOAT_EPSILON
            and scene.unit_settings.length_unit == "METERS",
        },
    }

    return {
        "collection": setup_collection.name if setup_collection is not None else None,
        "collection_exists": setup_collection is not None,
        "collection_objects": sorted(obj.name for obj in setup_objects),
        "active_camera": active_camera.name if active_camera is not None else None,
        "tagged_cameras": [
            {
                "name": obj.name,
                "type": obj.type,
                "in_setup_collection": obj in setup_objects,
                "active": obj == active_camera,
                "enabled": not obj.hide_render,
            }
            for obj in tagged_cameras
        ],
        "tagged_lights": [
            {
                "name": obj.name,
                "object_type": obj.type,
                "light_type": obj.data.type if obj.type == "LIGHT" else None,
                "in_setup_collection": obj in setup_objects,
                "enabled": not obj.hide_render,
                "energy": obj.data.energy if obj.type == "LIGHT" else None,
            }
            for obj in tagged_lights
        ],
        "enabled_lights": sorted(obj.name for obj in enabled_lights),
        "tagged_backdrops": [
            {
                "name": obj.name,
                "type": obj.type,
                "in_setup_collection": obj in setup_objects,
                "enabled": not obj.hide_render,
                "material_slots": len(obj.material_slots) if obj.type == "MESH" else 0,
            }
            for obj in tagged_backdrops
        ],
        "render_checks": render_checks,
        "framing": framing,
    }


def append_issue(issues: dict[str, Any], name: str, value: Any) -> None:
    # Numeric zero can itself be the evidence (for example, zero intended rigs),
    # so only the explicit boolean False and empty containers mean "no issue".
    if value is None or value is False or value == "" or value == [] or value == {}:
        return
    issues[name] = value


def issue_count(value: Any) -> int:
    if isinstance(value, dict):
        return max(1, sum(issue_count(item) for item in value.values()))
    if isinstance(value, (list, tuple, set)):
        return max(1, sum(issue_count(item) for item in value))
    return 1


def main() -> int:
    options = arguments()
    scene = bpy.context.scene
    objects = list(bpy.data.objects)
    all_meshes = [obj for obj in objects if obj.type == "MESH"]
    all_armatures = [obj for obj in objects if obj.type == "ARMATURE"]
    custom_shapes = bone_custom_shapes(all_armatures)

    rig_collection = bpy.data.collections.get(RIG_COLLECTION)
    mesh_collection = bpy.data.collections.get(MESH_COLLECTION)
    setup_collection = bpy.data.collections.get(SETUP_COLLECTION)
    rig_objects = collection_objects(rig_collection)
    mesh_objects = collection_objects(mesh_collection)
    setup_objects = collection_objects(setup_collection)
    intended_rigs = {obj for obj in rig_objects if obj.type == "ARMATURE"}
    character_meshes = [obj for obj in mesh_objects if obj.type == "MESH"]

    mesh_reports = [
        audit_mesh(
            obj,
            intended_rigs,
            obj in character_meshes,
            obj in custom_shapes,
            obj in setup_objects,
        )
        for obj in all_meshes
    ]
    character_mesh_reports = [item for item in mesh_reports if item["is_character_mesh"]]
    armature_reports = [audit_armature(obj) for obj in all_armatures]
    material_reports = [audit_material(material) for material in bpy.data.materials]
    image_reports = [audit_image(image) for image in bpy.data.images]
    studio = audit_studio(scene, setup_collection, character_meshes)

    issues: dict[str, Any] = {}
    append_issue(issues, "missing_rig_collection", rig_collection is None)
    append_issue(issues, "missing_mesh_collection", mesh_collection is None)
    append_issue(issues, "rig_armature_count", None if len(intended_rigs) == 1 else len(intended_rigs))
    append_issue(issues, "character_mesh_count", None if character_meshes else 0)
    append_issue(
        issues,
        "armatures_outside_rig_collection",
        sorted(obj.name for obj in all_armatures if obj not in intended_rigs),
    )
    append_issue(
        issues,
        "non_mesh_objects_in_mesh_collection",
        sorted(obj.name for obj in mesh_objects if obj.type != "MESH"),
    )
    append_issue(
        issues,
        "custom_bone_shapes_in_character_collection",
        sorted(obj.name for obj in custom_shapes if obj in character_meshes),
    )
    append_issue(
        issues,
        "unbound_character_meshes",
        sorted(
            item["name"]
            for item in character_mesh_reports
            if item["valid_intended_rig_modifiers"] == 0
        ),
    )
    append_issue(
        issues,
        "invalid_character_armature_modifiers",
        {
            item["name"]: item["invalid_armature_modifiers"]
            for item in character_mesh_reports
            if item["invalid_armature_modifiers"]
        },
    )
    append_issue(
        issues,
        "character_vertices_without_positive_weights",
        {
            item["name"]: item["vertices_without_positive_weights"]
            for item in character_mesh_reports
            if item["vertices_without_positive_weights"]
        },
    )
    append_issue(
        issues,
        "character_vertices_without_valid_rig_weights",
        {
            item["name"]: item["vertices_without_valid_rig_weights"]
            for item in character_mesh_reports
            if item["vertices_without_valid_rig_weights"]
        },
    )
    append_issue(
        issues,
        "invalid_positive_weight_assignments",
        {
            item["name"]: item["invalid_positive_weight_assignments"]
            for item in character_mesh_reports
            if item["invalid_positive_weight_assignments"]
        },
    )
    append_issue(
        issues,
        "character_meshes_without_material_slots",
        sorted(item["name"] for item in character_mesh_reports if item["zero_material_slots"]),
    )
    append_issue(
        issues,
        "placeholder_character_material_slots",
        {
            item["name"]: item["placeholder_material_slots"]
            for item in character_mesh_reports
            if item["placeholder_material_slots"]
        },
    )
    append_issue(
        issues,
        "missing_images",
        sorted(item["name"] for item in image_reports if item["external_missing"]),
    )
    append_issue(
        issues,
        "unpacked_file_images",
        sorted(
            item["name"]
            for item in image_reports
            if item["source"] in {"FILE", "TILED"} and not item["packed"]
        ),
    )
    append_issue(
        issues,
        "linked_external_datablocks",
        sorted(
            f"{collection_name}:{datablock.name}"
            for collection_name in (
                "armatures",
                "images",
                "materials",
                "meshes",
                "node_groups",
                "objects",
                "texts",
                "worlds",
            )
            for datablock in getattr(bpy.data, collection_name)
            if datablock.library is not None
        ),
    )

    append_issue(issues, "missing_studio_collection", not studio["collection_exists"])
    tagged_cameras = studio["tagged_cameras"]
    append_issue(
        issues,
        "tagged_studio_camera_count",
        None if len(tagged_cameras) == 1 else len(tagged_cameras),
    )
    if len(tagged_cameras) == 1:
        camera = tagged_cameras[0]
        append_issue(issues, "studio_camera_wrong_type", camera["type"] != "CAMERA")
        append_issue(issues, "studio_camera_not_active", not camera["active"])
        append_issue(issues, "studio_camera_not_in_setup_collection", not camera["in_setup_collection"])
        append_issue(issues, "studio_camera_disabled", not camera["enabled"])

    tagged_lights = studio["tagged_lights"]
    append_issue(
        issues,
        "tagged_studio_light_count",
        None if len(tagged_lights) == EXPECTED_STUDIO_LIGHTS else len(tagged_lights),
    )
    append_issue(
        issues,
        "invalid_tagged_studio_lights",
        [
            light
            for light in tagged_lights
            if light["object_type"] != "LIGHT"
            or light["light_type"] != "AREA"
            or not light["in_setup_collection"]
            or not light["enabled"]
            or light["energy"] is None
            or light["energy"] <= 0
        ],
    )
    tagged_light_names = {light["name"] for light in tagged_lights}
    append_issue(
        issues,
        "enabled_nonstudio_lights",
        sorted(name for name in studio["enabled_lights"] if name not in tagged_light_names),
    )

    tagged_backdrops = studio["tagged_backdrops"]
    append_issue(
        issues,
        "tagged_studio_backdrop_count",
        None if len(tagged_backdrops) == 1 else len(tagged_backdrops),
    )
    if len(tagged_backdrops) == 1:
        backdrop = tagged_backdrops[0]
        append_issue(issues, "studio_backdrop_wrong_type", backdrop["type"] != "MESH")
        append_issue(issues, "studio_backdrop_not_in_setup_collection", not backdrop["in_setup_collection"])
        append_issue(issues, "studio_backdrop_disabled", not backdrop["enabled"])
        append_issue(issues, "studio_backdrop_has_no_material", backdrop["material_slots"] == 0)

    append_issue(
        issues,
        "invalid_render_settings",
        {
            name: {"actual": item["actual"], "expected": item["expected"]}
            for name, item in studio["render_checks"].items()
            if not item["valid"]
        },
    )
    append_issue(
        issues,
        "character_outside_camera_frame",
        not studio["framing"]["all_bounds_in_frame"],
    )

    build_report_name = scene.get("xivblend_build_report_text")
    append_issue(
        issues,
        "missing_embedded_build_report",
        not isinstance(build_report_name, str) or bpy.data.texts.get(build_report_name) is None,
    )
    append_issue(
        issues,
        "scene_validation_not_passed",
        scene.get("xivblend_validation") != "passed",
    )

    orphan_counts: dict[str, int] = {}
    for name in (
        "actions",
        "armatures",
        "cameras",
        "images",
        "lights",
        "materials",
        "meshes",
        "node_groups",
        "texts",
        "worlds",
    ):
        orphan_counts[name] = sum(block.users == 0 for block in getattr(bpy.data, name))

    strict_count = sum(issue_count(value) for value in issues.values())
    strict_passed = strict_count == 0
    report = {
        "file": str(Path(bpy.data.filepath)),
        "blender": bpy.app.version_string,
        "strict": {
            "status": "pass" if strict_passed else "fail",
            "passed": strict_passed,
            "issue_count": strict_count,
            "report_only": options.report_only,
        },
        "scene": {
            "name": scene.name,
            "builder": scene.get("xivblend_builder", scene.get("clean_extract_builder")),
            "builder_version": scene.get(
                "xivblend_builder_version",
                scene.get("clean_extract_builder_version"),
            ),
            "material_mode": scene.get(
                "xivblend_material_mode",
                scene.get("clean_extract_material_mode"),
            ),
            "mapped_materials": scene.get(
                "xivblend_mapped_materials",
                scene.get("clean_extract_mapped_materials"),
            ),
            "validation": scene.get("xivblend_validation"),
            "frame_range": [scene.frame_start, scene.frame_end],
            "render_engine": scene.render.engine,
            "resolution": [
                scene.render.resolution_x,
                scene.render.resolution_y,
                scene.render.resolution_percentage,
            ],
            "camera": scene.camera.name if scene.camera is not None else None,
            "world": scene.world.name if scene.world is not None else None,
            "view_transform": scene.view_settings.view_transform,
            "look": scene.view_settings.look,
        },
        "counts": {
            "objects": len(objects),
            "object_types": dict(sorted(Counter(obj.type for obj in objects).items())),
            "collections": len(bpy.data.collections),
            "meshes": len(all_meshes),
            "character_meshes": len(character_meshes),
            "custom_bone_shapes": len(custom_shapes),
            "scene_setup_meshes": len(
                [obj for obj in all_meshes if obj in setup_objects]
            ),
            "armatures": len(all_armatures),
            "intended_rigs": len(intended_rigs),
            "materials": len(bpy.data.materials),
            "images": len(bpy.data.images),
            "actions": len(bpy.data.actions),
            "lights": len(bpy.data.lights),
            "cameras": len(bpy.data.cameras),
        },
        "collections": {
            collection.name: sorted(obj.name for obj in collection.objects)
            for collection in bpy.data.collections
        },
        "armatures": armature_reports,
        "actions": [
            {
                "name": action.name,
                "users": action.users,
                "frame_range": list(action.frame_range),
                "slots": len(action.slots),
                "layers": len(action.layers),
            }
            for action in bpy.data.actions
        ],
        "meshes": mesh_reports,
        "materials": material_reports,
        "images": image_reports,
        "studio": studio,
        "orphan_counts": orphan_counts,
        "observations": {
            "materials_with_empty_optional_image_nodes": [
                item["name"]
                for item in material_reports
                if item["empty_optional_image_nodes"]
            ],
            "character_meshes_with_empty_vertex_groups": {
                item["name"]: item["empty_vertex_groups"]
                for item in character_mesh_reports
                if item["empty_vertex_groups"]
            },
        },
        "issues": issues,
    }

    print(
        "[xivblend-audit] "
        + json.dumps(report, ensure_ascii=False, sort_keys=True),
        flush=True,
    )
    if not strict_passed and not options.report_only:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
