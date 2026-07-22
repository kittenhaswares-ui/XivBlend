"""Headless regression test for temporary Pizza/Apple prop materials.

Run with Blender, not the system Python::

    blender --background --factory-startup --python-exit-code 1 \
      --python tools/test_runtime_prop_materials.py
"""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import warnings

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


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def load_addon():
    spec = importlib.util.spec_from_file_location("xivblend_animation_browser", ADDON)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load the animation browser from {ADDON}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _image(name: str, color: tuple[float, float, float, float]):
    image = bpy.data.images.new(name, width=1, height=1, alpha=True)
    image.generated_color = color
    return image


def _linked_texture(nodes, links, name: str, label: str, image=None):
    texture = nodes.new("ShaderNodeTexImage")
    texture.name = name
    texture.label = label
    texture.image = image
    sink = nodes.new("ShaderNodeMixRGB")
    sink.name = f"{name} Test Sink"
    links.new(texture.outputs["Color"], sink.inputs[1])
    require(texture.outputs["Color"].is_linked, f"test setup did not link {name}")
    return texture


def _prop(name: str):
    mesh = bpy.data.meshes.new(f"{name} Mesh")
    mesh.from_pydata(
        [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0)],
        [],
        [(0, 1, 2)],
    )
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)

    material = bpy.data.materials.new(f"{name} Mapped FFXIV Material")
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", DeprecationWarning)
        material.use_nodes = True
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    nodes.clear()
    source_material = bpy.data.materials.new(f"{name} Source Material")
    source_material["ShaderPackage"] = "character.shpk"
    obj.data.materials.append(source_material)

    originals = {
        role: _image(f"{name} {role.title()} Image", color)
        for role, color in {
            "diffuse": (0.8, 0.2, 0.1, 1.0),
            "normal": (0.5, 0.5, 1.0, 1.0),
            "mask": (0.4, 0.4, 0.4, 1.0),
            "decal": (0.1, 0.8, 0.2, 1.0),
        }.items()
    }
    textures = {
        role: _linked_texture(
            nodes,
            links,
            f"{name} {role.title()}",
            f"{role.title()} Texture",
            image,
        )
        for role, image in originals.items()
    }
    textures["missing_decal"] = _linked_texture(
        nodes,
        links,
        f"{name} Missing Decal",
        "Optional Decal Texture",
    )
    textures["unlinked_decal"] = nodes.new("ShaderNodeTexImage")
    textures["unlinked_decal"].name = f"{name} Unlinked Decal"
    textures["unlinked_decal"].label = "Optional Decal Texture"

    # These missing samplers are linked on purpose. They prove the fallback is
    # selected by semantic role, not merely by an empty Image Texture socket.
    for role in ("diffuse", "normal", "mask"):
        textures[f"missing_{role}"] = _linked_texture(
            nodes,
            links,
            f"{name} Missing {role.title()}",
            f"{role.title()} Texture",
        )
    return obj, source_material, material, originals, textures


def main() -> None:
    if not bpy.app.background or getattr(bpy.app, "factory_startup", False) is False:
        raise RuntimeError("Run this test with --background --factory-startup")

    sys.dont_write_bytecode = True
    addon = load_addon()
    pizza, pizza_source, pizza_material, pizza_images, pizza_nodes = _prop("Pizza")
    apple, apple_source, apple_material, apple_images, apple_nodes = _prop("Apple")
    name_collision = _image(
        "XivBlend Runtime Transparent Optional Texture", (0.0, 0.0, 0.0, 1.0)
    )

    replacements = {
        pizza_source: pizza_material,
        apple_source: apple_material,
    }

    class _Version:
        @staticmethod
        def updateCurrentRelease():
            return None

    class _BlendImport:
        @staticmethod
        def import_shaders():
            return None

    class _NodeConfigs:
        @staticmethod
        def map_mesh(source_material, slots, _cache_directory):
            for slot in slots:
                slot.material = replacements[source_material]

    addon._material_runtime_modules = lambda: (_Version, _BlendImport, _NodeConfigs)
    snapshot = addon._data_snapshot()

    mapped = addon._map_runtime_prop_materials(
        [pizza, apple], ROOT / "work" / "test-prop-cache", snapshot
    )
    require(mapped == 2, f"expected two mapped prop materials, got {mapped}")
    require(
        pizza.material_slots[0].material is pizza_material,
        "Pizza did not receive its mapped material",
    )
    require(
        apple.material_slots[0].material is apple_material,
        "Apple did not receive its mapped material",
    )

    fallbacks = [
        image
        for image in bpy.data.images
        if image.get("xivblend_component") == "runtime_optional_texture_fallback"
    ]
    require(len(fallbacks) == 1, f"expected one runtime fallback, got {len(fallbacks)}")
    fallback = fallbacks[0]
    require(fallback is not None, "runtime transparent fallback image was not created")
    require(fallback is not name_collision, "a same-named user image was reused as the fallback")
    require(
        tuple(round(value, 6) for value in name_collision.generated_color)
        == (0.0, 0.0, 0.0, 1.0),
        "the same-named user image was modified",
    )
    require(tuple(fallback.size) == (1, 1), "runtime fallback is not a 1x1 image")
    require(
        tuple(round(value, 6) for value in fallback.generated_color) == (0.0, 0.0, 0.0, 0.0),
        "runtime fallback is not transparent black",
    )
    require(
        fallback.get("xivblend_component") == "runtime_optional_texture_fallback",
        "runtime fallback lost its component marker",
    )
    require(
        bool(fallback.get(addon.TRANSIENT_PROPERTY, False)),
        "runtime fallback is not marked transient",
    )

    for prop_name, originals, textures in (
        ("Pizza", pizza_images, pizza_nodes),
        ("Apple", apple_images, apple_nodes),
    ):
        require(
            textures["missing_decal"].image is fallback,
            f"{prop_name} linked missing decal did not receive the fallback",
        )
        require(
            textures["unlinked_decal"].image is None,
            f"{prop_name} unlinked decal was modified",
        )
        for role in ("diffuse", "normal", "mask", "decal"):
            require(
                textures[role].image is originals[role],
                f"{prop_name} real {role} texture was replaced",
            )
        for role in ("diffuse", "normal", "mask"):
            require(
                textures[f"missing_{role}"].image is None,
                f"{prop_name} missing {role} sampler incorrectly received a decal fallback",
            )

    require(
        addon._bind_runtime_optional_texture_fallbacks([pizza, apple]) == 0,
        "runtime fallback binding is not idempotent",
    )
    require(
        len(
            [
                image
                for image in bpy.data.images
                if image.get("xivblend_component") == "runtime_optional_texture_fallback"
            ]
        ) == 1,
        "runtime fallback binding created duplicate images",
    )
    print("XIVBLEND_RUNTIME_PROP_MATERIAL_TEST=PASS")


if __name__ == "__main__":
    main()
