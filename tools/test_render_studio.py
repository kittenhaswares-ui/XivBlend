"""Headless regression test for XivBlend's Blender Render Studio controls.

Run with Blender, not the system Python::

    blender --background --factory-startup --python-exit-code 1 \
      --python tools/test_render_studio.py
"""

from __future__ import annotations

import importlib.util
import math
from pathlib import Path
import sys
import tempfile

import bpy


ROOT = Path(__file__).resolve().parents[1]
ADDON = (
    ROOT
    / "Meddle"
    / "Meddle.Plugin"
    / "XivBlendBuilder"
    / "xivblend_animation_browser"
    / "__init__.py"
)
BUILDER = (
    ROOT
    / "Meddle"
    / "Meddle.Plugin"
    / "XivBlendBuilder"
    / "build_character.py"
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def close(actual: float, expected: float) -> bool:
    return math.isclose(float(actual), float(expected), rel_tol=1.0e-6, abs_tol=1.0e-6)


def load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load {name} from {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_addon():
    return load_module("xivblend_animation_browser", ADDON)


def verify_fresh_export_render_settings() -> None:
    """Exercise the builder setup used by newly exported character files."""
    builder = load_module("xivblend_build_character", BUILDER)
    scene = bpy.context.scene

    character = bpy.data.collections.new("Test FFXIV Character")
    setup = bpy.data.collections.new("Test Scene Setup")
    scene.collection.children.link(character)
    scene.collection.children.link(setup)

    mesh_data = bpy.data.meshes.new("Test Character Mesh")
    mesh_data.from_pydata(
        [
            (-0.5, -0.25, 0.0),
            (0.5, -0.25, 0.0),
            (0.5, 0.25, 2.0),
            (-0.5, 0.25, 2.0),
        ],
        [],
        [(0, 1, 2, 3)],
    )
    mesh_data.update()
    mesh = bpy.data.objects.new("Test Character Mesh", mesh_data)
    character.objects.link(mesh)

    armature_data = bpy.data.armatures.new("Test Character Rig")
    armature = bpy.data.objects.new("Test Character Rig", armature_data)
    character.objects.link(armature)

    builder.configure_scene_setup(scene, [mesh, armature], setup, character)
    require(scene.cycles.max_bounces == 8, "fresh export changed ordinary max bounces")
    require(
        scene.cycles.transparent_max_bounces == 128,
        "fresh export does not preserve deeply layered alpha-card hair or fur",
    )
    studio_lights = [
        obj
        for obj in setup.objects
        if obj.type == "LIGHT" and obj.get("xivblend_component") == "studio_light"
    ]
    require(len(studio_lights) == 3, "fresh export did not create three studio lights")
    for light in studio_lights:
        for attribute in ("energy", "size", "size_y", "spread"):
            stored = light.get(f"xivblend_beauty_{attribute}")
            require(stored is not None, f"fresh export omitted Beauty {attribute} baseline")
            require(
                close(stored, getattr(light.data, attribute)),
                f"fresh export stored the wrong Beauty {attribute} baseline",
            )
        for attribute, actual in (
            ("location", light.location),
            ("rotation_euler", light.rotation_euler),
            ("color", light.data.color),
        ):
            stored = light.get(f"xivblend_beauty_{attribute}")
            require(stored is not None, f"fresh export omitted Beauty {attribute} baseline")
            require(
                len(stored) == 3
                and all(close(value, wanted) for value, wanted in zip(stored, actual)),
                f"fresh export stored the wrong Beauty {attribute} baseline",
            )
        require(
            "xivblend_beauty_use_shadow" in light
            and
            bool(light.get("xivblend_beauty_use_shadow"))
            == bool(light.data.use_shadow),
            "fresh export stored the wrong Beauty shadow baseline",
        )
    require(len(scene.get("xivblend_studio_center", ())) == 3, "studio center is missing")
    require(scene.get("xivblend_studio_scale", 0.0) > 0.0, "studio scale is missing")
    require(scene.world.get("xivblend_component") == "studio_world", "studio world is untagged")
    require(
        close(scene.world.get("xivblend_beauty_strength"), 0.045),
        "studio world omitted its Beauty strength baseline",
    )


def create_legacy_studio(scene):
    """Build the old-file case: tagged lights exist but baseline props do not."""
    world = bpy.data.worlds.new("Neutral World")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.045
    scene.world = world

    values = {
        "key": (100.0, 2.0, 3.0, 1.4),
        "fill": (20.0, 4.0, 5.0, 1.2),
        "rim": (40.0, 1.0, 4.5, 1.0),
    }
    colors = {
        "key": (0.91, 0.67, 0.43),
        "fill": (0.31, 0.58, 0.88),
        "rim": (0.72, 0.28, 0.91),
    }
    shadows = {"key": True, "fill": False, "rim": True}
    lights = {}
    for role, (energy, size, size_y, spread) in values.items():
        data = bpy.data.lights.new(f"{role.title()} Light Test Data", "AREA")
        data.shape = "RECTANGLE"
        data.energy = energy
        data.size = size
        data.size_y = size_y
        data.spread = spread
        data.color = colors[role]
        data.use_shadow = shadows[role]
        obj = bpy.data.objects.new(f"{role.title()} Light Test", data)
        scene.collection.objects.link(obj)
        obj["xivblend_component"] = "studio_light"
        obj["xivblend_studio_role"] = role
        lights[role] = obj

    user_data = bpy.data.lights.new("User Lamp Data", "POINT")
    user_data.energy = 321.0
    # A user's unrelated point light may have a studio-like name. Legacy name
    # fallback must remain limited to XivBlend's old AREA lights.
    user_light = bpy.data.objects.new("Key Light User Point", user_data)
    scene.collection.objects.link(user_light)
    user_area_data = bpy.data.lights.new("Key Light User Area Data", "AREA")
    user_area_data.energy = 654.0
    user_area = bpy.data.objects.new("Key Light User Area", user_area_data)
    user_area.location = (7.0, 8.0, 9.0)
    scene.collection.objects.link(user_area)
    return lights, values, user_light, user_area


def create_material_fixture(scene, name, shader, value, subsurface, *, linked=False):
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    material["ShaderPackage"] = shader
    material["GetMaterialValue"] = value
    principled = material.node_tree.nodes.get("Principled BSDF")
    socket = principled.inputs["Subsurface Weight"]
    socket.default_value = subsurface
    upstream = None
    if linked:
        upstream = material.node_tree.nodes.new("ShaderNodeValue")
        upstream.outputs[0].default_value = subsurface
        material.node_tree.links.new(upstream.outputs[0], socket)

    mesh_data = bpy.data.meshes.new(f"{name} Mesh")
    mesh_data.from_pydata([(0.0, 0.0, 0.0), (0.1, 0.0, 0.0), (0.0, 0.1, 0.0)], [], [(0, 1, 2)])
    mesh = bpy.data.objects.new(f"{name} Object", mesh_data)
    mesh_data.materials.append(material)
    scene.collection.objects.link(mesh)
    return material, principled, upstream


class _RestrictedData:
    """Fail loudly if add-on registration tries to inspect the open file."""

    def __init__(self, accesses: list[str]):
        self._accesses = accesses

    def __getattr__(self, name: str):
        self._accesses.append(name)
        raise AssertionError(
            f"register() accessed bpy.data.{name} while Blender data was restricted"
        )


class _RestrictedBpy:
    """Delegate Blender's registration API but expose _RestrictData semantics."""

    def __init__(self, wrapped, accesses: list[str]):
        self._wrapped = wrapped
        self.data = _RestrictedData(accesses)

    def __getattr__(self, name: str):
        return getattr(self._wrapped, name)


def main() -> None:
    if not bpy.app.background or getattr(bpy.app, "factory_startup", False) is False:
        raise RuntimeError("Run this test with --background --factory-startup")

    # Simulate a file saved by the retired clay-preview implementation. Blender
    # exposes bpy.data as _RestrictData while enabling an add-on, so register()
    # must defer this file migration until load_post.
    legacy = bpy.data.materials.new("XivBlend Smooth Animation Preview")
    legacy["xivblend_runtime_preview_material"] = True
    legacy_name = legacy.name
    bpy.context.scene.view_layers[0].material_override = legacy

    sys.dont_write_bytecode = True
    addon = load_addon()
    restricted_accesses: list[str] = []
    addon.bpy = _RestrictedBpy(bpy, restricted_accesses)
    registration_error = None
    try:
        addon.register()
    except Exception as error:
        registration_error = error
    finally:
        addon.bpy = bpy

    if registration_error is not None:
        # register() adds classes before its final setup steps. Make a failed
        # test repeatable in the same Blender process by undoing partial state.
        try:
            addon.unregister()
        except Exception:
            pass
        raise RuntimeError(
            f"add-on registration failed in _RestrictData context: {registration_error}"
        ) from registration_error

    try:
        scene = bpy.context.scene
        require(not restricted_accesses, f"register inspected bpy.data: {restricted_accesses}")
        require(
            scene.view_layers[0].material_override is legacy,
            "legacy file migration ran during restricted registration",
        )
        require(
            bpy.data.materials.get(legacy_name) is legacy,
            "legacy material was removed during restricted registration",
        )

        require(
            "xivblend_render_quality" not in scene,
            "register persisted a Render Studio preset into an unrelated file",
        )
        scene.render.engine = "CYCLES"
        scene.view_settings.exposure = 1.25
        addon._load_post_handler(None)
        require(scene.view_layers[0].material_override is None, "legacy clay override survived")
        require(bpy.data.materials.get(legacy_name) is None, "legacy clay material survived")
        require(scene.render.engine == "CYCLES", "load_post changed an unrelated file's engine")
        require(close(scene.view_settings.exposure, 1.25), "load_post changed unrelated exposure")

        lights, beauty_values, user_light, user_area = create_legacy_studio(scene)
        user_energy = float(user_light.data.energy)
        user_area_state = (float(user_area.data.energy), tuple(user_area.location))
        scene["xivblend_studio_center"] = (0.25, -0.50, 1.00)
        scene["xivblend_studio_scale"] = 2.0
        face, face_principled, _ = create_material_fixture(
            scene, "Eligible Face", "skin.shpk", "GetMaterialValueFace", 0.27
        )
        body, body_principled, _ = create_material_fixture(
            scene, "Body Skin", "skin.shpk", "GetMaterialValueBody", 0.33
        )
        linked_face, linked_principled, linked_value = create_material_fixture(
            scene,
            "Linked Face",
            "skin.shpk",
            "GetMaterialValueFace",
            0.41,
            linked=True,
        )
        low_sss_face, low_sss_principled, _ = create_material_fixture(
            scene, "Low SSS Face", "skin.shpk", "GetMaterialValueFace", 0.04
        )
        orphan = bpy.data.materials.new("Orphan Face")
        orphan.use_nodes = True
        orphan["ShaderPackage"] = "skin.shpk"
        orphan["GetMaterialValue"] = "GetMaterialValueFace"
        orphan_socket = orphan.node_tree.nodes["Principled BSDF"].inputs[
            "Subsurface Weight"
        ]
        orphan_socket.default_value = 0.29

        scene.xivblend_render_quality = "ANIMATE"
        require(scene.render.engine == "BLENDER_EEVEE", "Animate did not retain Eevee renders")

        scene.xivblend_render_quality = "BEAUTY"
        require(scene.render.engine == "CYCLES", "Beauty did not select Cycles")
        require(scene.cycles.samples == 256, "Beauty sample count changed")
        require(scene.cycles.adaptive_min_samples == 16, "Beauty minimum samples changed")
        require(scene.cycles.texture_limit == "2048", "viewport texture limit changed")
        require(scene.cycles.max_bounces == 8, "Beauty changed ordinary max bounces")
        require(
            scene.cycles.transparent_max_bounces == 128,
            "Beauty does not preserve deeply layered alpha-card hair or fur",
        )
        require(
            close(
                face_principled.inputs["Subsurface Weight"].default_value,
                addon.FACE_SKIN_RENDER_SSS_WEIGHT,
            ),
            "Beauty did not reduce broad face subsurface wash",
        )
        require(
            close(low_sss_principled.inputs["Subsurface Weight"].default_value, 0.04),
            "Beauty increased an intentionally low face subsurface value",
        )
        require(
            close(body_principled.inputs["Subsurface Weight"].default_value, 0.33),
            "Beauty changed body skin",
        )
        require(
            linked_principled.inputs["Subsurface Weight"].is_linked
            and close(linked_value.outputs[0].default_value, 0.41),
            "Beauty changed a linked face-skin input",
        )
        require(close(orphan_socket.default_value, 0.29), "Beauty changed an orphan material")
        beauty_transforms = {
            role: {
                "location": tuple(light.location),
                "rotation": tuple(light.rotation_euler),
                "color": tuple(light.data.color),
                "shadow": bool(light.data.use_shadow),
            }
            for role, light in lights.items()
        }
        for role, expected in beauty_values.items():
            light = lights[role]
            actual = (light.data.energy, light.data.size, light.data.size_y, light.data.spread)
            require(
                all(close(value, wanted) for value, wanted in zip(actual, expected)),
                f"Beauty did not preserve the {role} light baseline",
            )

        scene.xivblend_render_quality = "DRAMATIC"
        require(scene.render.engine == "CYCLES", "Dramatic Detail did not select Cycles")
        require(scene.cycles.samples == 256, "Dramatic Detail changed the sample budget")
        require(scene.cycles.max_bounces == 8, "Dramatic Detail changed ordinary max bounces")
        require(
            scene.cycles.transparent_max_bounces == 128,
            "Dramatic Detail lost the dense alpha-card fix",
        )
        dramatic_values = {}
        for role, baseline in beauty_values.items():
            light = lights[role]
            factors = addon.DRAMATIC_LIGHT_PROFILE[role]
            expected = tuple(
                baseline[index] * factors[attribute]
                for index, attribute in enumerate(("energy", "size", "size_y", "spread"))
            )
            actual = (light.data.energy, light.data.size, light.data.size_y, light.data.spread)
            require(
                all(close(value, wanted) for value, wanted in zip(actual, expected)),
                f"Dramatic Detail configured the {role} light incorrectly",
            )
            require(light.data.use_shadow, f"Dramatic Detail disabled {role} shadows")
            dramatic_values[role] = actual
        require(close(scene.world.node_tree.nodes["Background"].inputs["Strength"].default_value, 0.018), "Dramatic Detail world fill changed")
        require(close(scene.view_settings.exposure, -0.55), "Dramatic Detail exposure changed")
        require(close(user_light.data.energy, user_energy), "Dramatic Detail changed a user light")

        scene.xivblend_render_quality = "MOOD"
        require(scene.render.engine == "CYCLES", "Mood did not select Cycles")
        center = addon.Vector((0.25, -0.50, 1.00))
        scale = 2.0
        target = center + addon.Vector((0.0, 0.0, 0.34 * scale))
        for role, light in lights.items():
            profile = addon.MOOD_LIGHT_PROFILE[role]
            expected_location = center + addon.Vector(profile["offset"]) * scale
            require(
                all(close(value, wanted) for value, wanted in zip(light.location, expected_location)),
                f"Mood placed the {role} light incorrectly",
            )
            require(
                close(light.data.energy, profile["energy"] * scale * scale)
                and close(light.data.size, profile["size"] * scale)
                and close(light.data.size_y, profile["size_y"] * scale)
                and close(light.data.spread, profile["spread"]),
                f"Mood configured the {role} softbox incorrectly",
            )
            require(
                bool(light.data.use_shadow) == bool(profile["use_shadow"]),
                f"Mood configured the {role} shadow incorrectly",
            )
            forward = light.rotation_euler.to_quaternion() @ addon.Vector((0.0, 0.0, -1.0))
            require(
                forward.normalized().dot((target - light.location).normalized()) > 0.99999,
                f"Mood did not aim the {role} light at the portrait",
            )
            require(
                all(
                    close(value, wanted)
                    for value, wanted in zip(light.data.color, addon.MOOD_LIGHT_COLORS[role])
                ),
                f"Mood configured the {role} color incorrectly",
            )
        require(
            close(
                scene.world.node_tree.nodes["Background"].inputs["Strength"].default_value,
                addon.MOOD_WORLD_STRENGTH,
            ),
            "Mood world fill changed",
        )
        require(
            close(scene.view_settings.exposure, addon.MOOD_EXPOSURE),
            "Mood exposure changed",
        )
        require(close(user_light.data.energy, user_energy), "Mood changed a user light")
        require(
            close(user_area.data.energy, user_area_state[0])
            and tuple(user_area.location) == user_area_state[1],
            "Mood changed an unrelated user area light",
        )
        require(
            close(
                face_principled.inputs["Subsurface Weight"].default_value,
                addon.FACE_SKIN_RENDER_SSS_WEIGHT,
            ),
            "Mood recaptured the already-active face baseline",
        )

        scene.xivblend_color_preset = "ACCURATE"
        require(
            scene.view_settings.view_transform == "Khronos PBR Neutral",
            "accurate-color transform changed",
        )
        require(
            close(scene.view_settings.exposure, addon.MOOD_EXPOSURE),
            "color preset erased Mood exposure",
        )
        require(
            all(all(close(channel, 1.0) for channel in light.data.color) for light in lights.values()),
            "accurate-color Mood lights are not neutral",
        )

        scene.xivblend_render_quality = "DRAMATIC"
        require(close(scene.view_settings.exposure, -0.55), "Detail-after-Mood exposure changed")
        require(
            all(
                all(
                    close(value, wanted)
                    for value, wanted in zip(
                        (
                            lights[role].data.energy,
                            lights[role].data.size,
                            lights[role].data.size_y,
                            lights[role].data.spread,
                        ),
                        dramatic_values[role],
                    )
                )
                for role in dramatic_values
            ),
            "color preset changed the Dramatic light profile",
        )

        scene.xivblend_render_quality = "BEAUTY"
        scene.xivblend_color_preset = "BEAUTY"
        for role, expected in beauty_values.items():
            light = lights[role]
            actual = (light.data.energy, light.data.size, light.data.size_y, light.data.spread)
            require(
                all(close(value, wanted) for value, wanted in zip(actual, expected)),
                f"Beauty did not restore the {role} light exactly",
            )
            restored = beauty_transforms[role]
            require(
                all(close(value, wanted) for value, wanted in zip(light.location, restored["location"]))
                and all(close(value, wanted) for value, wanted in zip(light.rotation_euler, restored["rotation"]))
                and all(close(value, wanted) for value, wanted in zip(light.data.color, restored["color"]))
                and bool(light.data.use_shadow) == restored["shadow"],
                f"Beauty did not restore the {role} transform, color and shadow",
            )
        require(close(scene.world.node_tree.nodes["Background"].inputs["Strength"].default_value, 0.045), "Beauty did not restore world fill")

        scene.xivblend_render_quality = "DRAMATIC"
        for role, expected in dramatic_values.items():
            light = lights[role]
            actual = (light.data.energy, light.data.size, light.data.size_y, light.data.spread)
            require(
                all(close(value, wanted) for value, wanted in zip(actual, expected)),
                f"repeated Dramatic selection drifted the {role} light",
            )

        scene.xivblend_render_quality = "PREVIEW"
        require(scene.render.engine == "BLENDER_EEVEE", "Preview did not restore Eevee")
        for role, expected in beauty_values.items():
            light = lights[role]
            actual = (light.data.energy, light.data.size, light.data.size_y, light.data.spread)
            require(
                all(close(value, wanted) for value, wanted in zip(actual, expected)),
                f"Preview did not restore the {role} Beauty light",
            )
        require(close(scene.world.node_tree.nodes["Background"].inputs["Strength"].default_value, 0.045), "Preview did not restore world fill")
        require(
            close(face_principled.inputs["Subsurface Weight"].default_value, 0.27),
            "Preview did not restore the original face subsurface value",
        )
        require(
            close(low_sss_principled.inputs["Subsurface Weight"].default_value, 0.04),
            "Preview did not restore the low-SSS face baseline",
        )
        face_principled.inputs["Subsurface Weight"].default_value = 0.19
        scene.xivblend_render_quality = "MOOD"
        scene.xivblend_render_quality = "PREVIEW"
        require(
            close(face_principled.inputs["Subsurface Weight"].default_value, 0.19),
            "face baseline did not refresh after a user edit in Preview",
        )

        # A material artist may rewire the active output while a final preset
        # is selected. Preview must still restore the exact Principled node
        # XivBlend changed, even when that node is no longer the active shader.
        scene.xivblend_render_quality = "MOOD"
        face_tree = face.node_tree
        face_output = next(
            node
            for node in face_tree.nodes
            if node.type == "OUTPUT_MATERIAL" and node.is_active_output
        )
        face_tree.links.remove(face_output.inputs["Surface"].links[0])
        replacement = face_tree.nodes.new("ShaderNodeBsdfDiffuse")
        face_tree.links.new(replacement.outputs["BSDF"], face_output.inputs["Surface"])
        scene.xivblend_render_quality = "PREVIEW"
        require(
            close(face_principled.inputs["Subsurface Weight"].default_value, 0.19)
            and not bool(face.get(addon.FACE_SKIN_PROFILE_ACTIVE_PROPERTY, False)),
            "Preview did not restore a face Principled node after output rewiring",
        )
        face_tree.links.remove(face_output.inputs["Surface"].links[0])
        face_tree.links.new(face_principled.outputs["BSDF"], face_output.inputs["Surface"])
        face_tree.nodes.remove(replacement)

        scene.xivblend_render_quality = "MOOD"
        unrelated_scene = bpy.data.scenes.new("Unrelated Save Scene")
        bpy.context.window.scene = unrelated_scene
        addon._save_pre_handler(None)
        require(
            close(face_principled.inputs["Subsurface Weight"].default_value, 0.19)
            and not bool(face.get(addon.FACE_SKIN_PROFILE_ACTIVE_PROPERTY, False)),
            "save_pre did not restore active face skin from another scene",
        )
        addon._resume_after_save()
        require(
            close(
                face_principled.inputs["Subsurface Weight"].default_value,
                addon.FACE_SKIN_RENDER_SSS_WEIGHT,
            )
            and bool(face.get(addon.FACE_SKIN_PROFILE_ACTIVE_PROPERTY, False)),
            "save_post did not resume the exact face profile from another scene",
        )
        bpy.context.window.scene = scene
        scene.xivblend_render_quality = "PREVIEW"

        # The save callback is delayed briefly by Blender. If the user changes
        # back to Preview in that gap, it must not resurrect final-mode SSS.
        scene.xivblend_render_quality = "MOOD"
        addon._save_pre_handler(None)
        scene.xivblend_render_quality = "PREVIEW"
        addon._resume_after_save()
        require(
            close(face_principled.inputs["Subsurface Weight"].default_value, 0.19)
            and not bool(face.get(addon.FACE_SKIN_PROFILE_ACTIVE_PROPERTY, False)),
            "delayed save resume reapplied face tuning after switching to Preview",
        )

        scene.xivblend_background_preset = "TRANSPARENT"
        require(scene.render.film_transparent, "transparent background did not enable film alpha")

        scene.xivblend_output_preset = "EXR"
        require(scene.render.image_settings.file_format == "OPEN_EXR", "EXR output changed")
        require(scene.render.image_settings.color_depth == "16", "EXR depth changed")

        # Simulate reopening a final-mode file: no enum callback fires, so the
        # load handler must migrate Cycles, rebuild Mood without drift and
        # reapply temporary face detail after saving the untouched baseline.
        scene.xivblend_render_quality = "MOOD"
        scene.cycles.transparent_max_bounces = 8
        with tempfile.TemporaryDirectory(prefix="xivblend-render-studio-") as folder:
            blend_path = str(Path(folder) / "saved-mood.blend")
            bpy.ops.wm.save_as_mainfile(filepath=blend_path, check_existing=False)
            bpy.ops.wm.open_mainfile(filepath=blend_path)
            scene = bpy.context.scene
            require(scene.xivblend_render_quality == "MOOD", "saved Mood mode was lost")
            require(
                scene.cycles.transparent_max_bounces == 128,
                "load_post did not migrate an already-selected Mood file",
            )
            require(scene.cycles.max_bounces == 8, "load_post changed ordinary max bounces")
            reopened_face = bpy.data.materials["Eligible Face"]
            reopened_socket = reopened_face.node_tree.nodes["Principled BSDF"].inputs[
                "Subsurface Weight"
            ]
            require(
                close(
                    reopened_socket.default_value,
                    addon.FACE_SKIN_RENDER_SSS_WEIGHT,
                ),
                "Mood reload lost face detail",
            )
            scene.xivblend_render_quality = "PREVIEW"
            require(
                close(reopened_socket.default_value, 0.19),
                "Preview after Mood reload did not restore the saved face baseline",
            )
    finally:
        addon.unregister()

    require(
        not hasattr(bpy.types.Scene, "xivblend_render_quality"),
        "add-on properties survived unregister",
    )
    verify_fresh_export_render_settings()
    print("XIVBLEND_RENDER_STUDIO_TEST=PASS")


if __name__ == "__main__":
    main()
